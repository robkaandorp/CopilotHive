using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using CopilotHive.Configuration;
using CopilotHive.Services;
using CopilotHive.Workers;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

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
        Assert.Null(config.Orchestrator.Model);
        Assert.Equal(10, config.Orchestrator.MaxIterations);
        Assert.Equal(3, config.Orchestrator.MaxRetriesPerTask);
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
        Assert.Null(config.Orchestrator.Model);
        Assert.Equal(20, config.Orchestrator.MaxIterations);
        Assert.Equal(3, config.Orchestrator.MaxRetriesPerTask);
    }

    [Fact]
    public void ParseConfig_MarksInstanceIsConfiguredTrue()
    {
        const string yaml = """
            version: "1.0"
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        // A repo-parsed config is marked IsConfigured = true (vs. the no-repo fallback false).
        Assert.True(config.IsConfigured);
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

    // ── Nullable models + blank→null normalization (Slice 3a) ────────────────

    [Fact]
    public void ParseConfig_OrchestratorModelOmitted_IsNull()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              max_iterations: 5
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Null(config.Orchestrator.Model);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ParseConfig_OrchestratorBlankModel_NormalizesToNull(string blank)
    {
        var yaml = $"""
            version: "1.0"
            orchestrator:
              model: "{blank}"
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Null(config.Orchestrator.Model);
    }

    [Fact]
    public void ParseConfig_WorkerRoleModelOmitted_IsNull()
    {
        const string yaml = """
            version: "1.0"
            workers:
              coder:
                context_window: 100000
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Null(config.Workers["coder"].Model);
    }

    [Fact]
    public void ParseConfig_WorkerRoleBlankModel_NormalizesToNull()
    {
        const string yaml = """
            version: "1.0"
            workers:
              coder:
                model: "   "
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.Null(config.Workers["coder"].Model);
    }

    [Fact]
    public void ParseConfig_ComposerModelOmitted_IsNull()
    {
        const string yaml = """
            version: "1.0"
            composer:
              max_steps: 50
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.NotNull(config.Composer);
        Assert.Null(config.Composer!.Model);
    }

    [Fact]
    public void ParseConfig_ComposerBlankModel_NormalizesToNull()
    {
        const string yaml = """
            version: "1.0"
            composer:
              model: ""
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        Assert.NotNull(config.Composer);
        Assert.Null(config.Composer!.Model);
    }

    // ── Retired composer.models key (Slice 3a) ─────────────────────────────────

    [Fact]
    public void ParseConfig_ComposerModelsKey_ThrowsFatalNamingTheKey()
    {
        const string yaml = """
            version: "1.0"
            composer:
              model: copilot/composer-model
              models:
                - copilot/composer-model
                - copilot/alt-model
            """;

        var ex = Assert.Throws<YamlDotNet.Core.YamlException>(() => ConfigRepoManager.ParseConfig(yaml));
        Assert.Contains("composer.models", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseConfig_ComposerModelsKeyEmptyList_ThrowsFatalNamingTheKey()
    {
        const string yaml = """
            version: "1.0"
            composer:
              model: copilot/composer-model
              models: []
            """;

        var ex = Assert.Throws<YamlDotNet.Core.YamlException>(() => ConfigRepoManager.ParseConfig(yaml));
        Assert.Contains("composer.models", ex.Message, StringComparison.Ordinal);
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
            "# Coder\nYou write great code.",
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        var content = await manager.LoadAgentsMdAsync(WorkerRole.Coder, TestContext.Current.CancellationToken);

        Assert.NotNull(content);
        Assert.Contains("You write great code.", content);
    }

    [Fact]
    public async Task LoadAgentsMdAsync_NonexistentRole_ReturnsNull()
    {
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        var content = await manager.LoadAgentsMdAsync(WorkerRole.MergeWorker, TestContext.Current.CancellationToken);

        Assert.Null(content);
    }

    [Fact]
    public async Task LoadAgentsMdAsync_CaseInsensitiveRole()
    {
        var agentsDir = Path.Combine(_tempDir, "agents");
        Directory.CreateDirectory(agentsDir);
        await File.WriteAllTextAsync(
            Path.Combine(agentsDir, "tester.agents.md"),
            "# Tester instructions",
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        var content = await manager.LoadAgentsMdAsync(WorkerRole.Tester, TestContext.Current.CancellationToken);

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
            """,
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var config = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1.0", config.Version);
        Assert.Single(config.Repositories);
        Assert.Equal("test-repo", config.Repositories[0].Name);
    }

    [Fact]
    public async Task LoadConfigAsync_MissingFile_ThrowsFileNotFound()
    {
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);

        await Assert.ThrowsAsync<FileNotFoundException>(() => manager.LoadConfigAsync(TestContext.Current.CancellationToken));
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
            """,
            TestContext.Current.CancellationToken);

        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var first = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        var second = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);

        Assert.Same(first, second);
    }

    // ── WriteConfigAsync tests ────────────────────────────────────────────────

    [Fact]
    public async Task WriteConfigAsync_SerializesWithSnakeCaseKeys()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "test-model" },
            Models = new ModelsConfig { CompactionModel = "mini-model" },
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("compaction_model:", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_OmitsNullDefaultValues()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "my-model" },
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("available_models", yaml);
        Assert.DoesNotContain("compaction_model", yaml);
        Assert.DoesNotContain("composer", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_UpdatesCachedConfig()
    {
        // Write initial config to disk so LoadConfigAsync can read it
        await File.WriteAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"),
            "version: \"1.0\"",
            TestContext.Current.CancellationToken);

        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        // Load once to populate cache
        var first = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Equal("1.0", first.Version);

        // Write new config
        var updated = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "updated-model" },
        };
        await manager.WriteConfigAsync(updated, TestContext.Current.CancellationToken);

        // Load again — should return the updated config from cache
        var second = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        Assert.Equal("updated-model", second.Orchestrator.Model);
    }

    // ── WriteConfigAsync snapshot serialization fidelity tests ───────────────

    /// <summary>
    /// Builds the STABLE representative live configuration used as the fidelity baseline.
    /// It must never be mutated by the tests: <see cref="ConfigRepoManager.WriteConfigAsync"/>
    /// serializes a detached snapshot of it, so the expected YAML is derived from this exact
    /// instance and any mutation inside the writer would change the bytes.
    /// </summary>
    private static HiveConfigFile MakeRepresentativeConfig()
    {
        var config = new HiveConfigFile
        {
            Version = "1.0",
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-app",
                    Url = "https://github.com/org/my-app.git",
                    DefaultBranch = "develop",
                    MonitorCi = true,
                    CiTimeoutMinutes = 45,
                    Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "v1" },
                    PublishNuGet = new NuGetPublishConfig
                    {
                        Packages = [new NuGetPackageEntry { PackageId = "My.Library" }]
                    }
                },
                new RepositoryConfig
                {
                    Name = "bare-repo",
                    Url = "https://github.com/org/bare.git"
                }
            ],
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new()
                {
                    Model = "coder-model",
                    PremiumModel = "coder-premium",
                    ContextWindow = 128000,
                    ReasoningEffort = "high",
                    PremiumReasoningEffort = "extra_high"
                },
                ["tester"] = new() { Model = "tester-model" }
            },
            Orchestrator = new OrchestratorConfig
            {
                Model = "brain-model",
                MaxIterations = 7,
                MaxRetriesPerTask = 2,
                MaxParallelGoals = 3,
                VerboseLogging = true,
                BrainMaxSteps = 25,
                BranchCleanupDelayHours = 12,
                WorkerTaskTimeoutMinutes = 90,
                ReasoningEffort = "medium"
            },
            Models = new ModelsConfig
            {
                CompactionModel = "compactor",
                // Raw catalog values: untrimmed names, a duplicate, every ModelEntry field
                // populated, nullable and non-positive context windows, nullable vision flags,
                // raw reasoning strings — WriteConfigAsync must persist them WITHOUT the
                // normalization that ParseConfig applies on read.
                AvailableModels =
                [
                    new ModelEntry { Name = "model-a", ContextWindow = 200000, ReasoningEffort = "high", Description = "primary", SupportsVision = true },
                    new ModelEntry { Name = "  model-b  ", ContextWindow = null, ReasoningEffort = null, Description = null, SupportsVision = null },
                    new ModelEntry { Name = "model-a", ContextWindow = -5, ReasoningEffort = "  low  ", Description = "dup", SupportsVision = false },
                    new ModelEntry { Name = "model-c", ContextWindow = 0 }
                ],
                SubAgentModels =
                [
                    new ModelEntry { Name = "sub-1", ContextWindow = 1000, SupportsVision = null }
                ]
            },
            Composer = new ComposerConfig
            {
                Model = "composer-model",
                MaxSteps = 7,
                ReasoningEffort = "low",
                EventNotifications = new EventNotificationsConfig
                {
                    Mode = "active",
                    ActiveEvents = ["goal_completed", "ci_failed"],
                    ThrottleSeconds = 45
                }
            }
        };
        return config;
    }

    /// <summary>
    /// The expected YAML: what the PRODUCTION serializer settings produce for the stable
    /// representative config. The reference is built with the identical builder chain the
    /// production <c>YamlSerializer</c> field uses (underscored naming, omit defaults/nulls).
    /// </summary>
    private static string ExpectedRepresentativeYaml() =>
        new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
            .Build()
            .Serialize(MakeRepresentativeConfig());

    /// <summary>Reads the persisted hive-config.yaml from disk (not the manager cache).</summary>
    private async Task<string> ReadWrittenYamlAsync(FakeConfigRepoManager manager)
    {
        var path = Path.Combine(_tempDir, "hive-config.yaml");
        Assert.True(File.Exists(path), "WriteConfigAsync did not create hive-config.yaml.");
        return await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Byte-wise fidelity: the file WriteConfigAsync persists must equal what the existing
    /// serializer settings produce for the stable representative LIVE config — proving the
    /// snapshot path is schema-, naming-, order-, and omission-compatible with the previous
    /// live serialization.
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_PersistedYaml_MatchesLiveSerializationByteForByte()
    {
        var config = MakeRepresentativeConfig();
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var disk = await ReadWrittenYamlAsync(manager);
        Assert.Equal(ExpectedRepresentativeYaml(), disk);
    }

    /// <summary>
    /// Field-level round-trip for the populated catalog and config sections of the persisted
    /// YAML: every populated top-level section, every <see cref="ModelEntry"/> field, and the
    /// raw catalog values (duplicate and untrimmed names, nullable/nonpositive windows,
    /// nullable vision flags, raw reasoning strings) must survive to disk unchanged.
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_PersistedYaml_RoundTripsPopulatedSectionsFieldByField()
    {
        var config = MakeRepresentativeConfig();
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var disk = await ReadWrittenYamlAsync(manager);
        var parsed = ConfigRepoManager.ParseConfig(disk);

        // Top-level sections all present.
        Assert.NotNull(parsed.Orchestrator);
        Assert.NotNull(parsed.Models);
        Assert.NotNull(parsed.Composer);
        Assert.Equal(2, parsed.Repositories.Count);
        Assert.Equal(2, parsed.Workers.Count);

        // Orchestrator.
        Assert.Equal("brain-model", parsed.Orchestrator.Model);
        Assert.Equal(7, parsed.Orchestrator.MaxIterations);
        Assert.Equal("medium", parsed.Orchestrator.ReasoningEffort);

        // Workers.
        Assert.Equal("coder-model", parsed.Workers["coder"].Model);
        Assert.Equal("coder-premium", parsed.Workers["coder"].PremiumModel);
        Assert.Equal(128000, parsed.Workers["coder"].ContextWindow);
        Assert.Equal("extra_high", parsed.Workers["coder"].PremiumReasoningEffort);

        // Composer.
        Assert.Equal("composer-model", parsed.Composer.Model);
        Assert.Equal(7, parsed.Composer.MaxSteps);
        Assert.Equal("low", parsed.Composer.ReasoningEffort);
        Assert.NotNull(parsed.Composer.EventNotifications);
        Assert.Equal("active", parsed.Composer.EventNotifications.Mode);
        Assert.Equal(["goal_completed", "ci_failed"], parsed.Composer.EventNotifications.ActiveEvents);

        // Catalog: every entry, RAW (no trimming, no dedup, no window clamping).
        var available = parsed.Models.AvailableModels!;
        Assert.Equal(4, available.Count);
        Assert.Equal("model-a", available[0].Name);
        Assert.Equal(200000, available[0].ContextWindow);
        Assert.Equal("high", available[0].ReasoningEffort);
        Assert.Equal("primary", available[0].Description);
        Assert.True(available[0].SupportsVision);
        // Untrimmed name preserved verbatim (serialization fidelity, NOT parser normalization —
        // ParseConfig never trims entry names, so the raw value round-trips).
        Assert.Equal("  model-b  ", available[1].Name);
        Assert.Null(available[1].ContextWindow);
        Assert.Null(available[1].ReasoningEffort);
        Assert.Null(available[1].Description);
        Assert.Null(available[1].SupportsVision);
        // Duplicate name preserved verbatim.
        Assert.Equal("model-a", available[2].Name);
        Assert.Equal(-5, available[2].ContextWindow);
        Assert.Equal("  low  ", available[2].ReasoningEffort);
        Assert.Equal("dup", available[2].Description);
        Assert.False(available[2].SupportsVision);
        // Non-positive window preserved.
        Assert.Equal("model-c", available[3].Name);
        Assert.Equal(0, available[3].ContextWindow);
        // Sub-agent list preserved separately.
        var sub = parsed.Models.SubAgentModels!;
        var entry = Assert.Single(sub);
        Assert.Equal("sub-1", entry.Name);
        Assert.Equal(1000, entry.ContextWindow);
        Assert.Null(entry.SupportsVision);
        // Compaction model round-trips.
        Assert.Equal("compactor", parsed.Models.CompactionModel);
    }

    /// <summary>
    /// A config WITHOUT a Models section serializes without one — the snapshot must not
    /// materialize an empty catalog section.
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_MissingModelsSection_OmitsModelsFromDisk()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "only-model" },
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var disk = await ReadWrittenYamlAsync(manager);
        Assert.Equal(
            new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
                .Build()
                .Serialize(config),
            disk);
        Assert.DoesNotContain("models", disk);
        Assert.DoesNotContain("available_models", disk);
        Assert.DoesNotContain("sub_agent_models", disk);
    }

    /// <summary>
    /// NULL catalog lists stay ABSENT from the persisted YAML while EMPTY lists serialize as
    /// empty sequences — the null/default-omission behavior is preserved through the snapshot.
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_NullVersusEmptyCatalogLists_DistinguishedOnDisk()
    {
        var withNull = new HiveConfigFile
        {
            Models = new ModelsConfig { CompactionModel = "m" }
        };
        var withEmpty = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                CompactionModel = "m",
                AvailableModels = [],
                SubAgentModels = []
            }
        };

        var nullDir = Path.Combine(_tempDir, "null-lists");
        var emptyDir = Path.Combine(_tempDir, "empty-lists");
        Directory.CreateDirectory(nullDir);
        Directory.CreateDirectory(emptyDir);
        var nullManager = new FakeConfigRepoManager("https://example.com/config.git", nullDir);
        var emptyManager = new FakeConfigRepoManager("https://example.com/config.git", emptyDir);

        await nullManager.WriteConfigAsync(withNull, TestContext.Current.CancellationToken);
        await emptyManager.WriteConfigAsync(withEmpty, TestContext.Current.CancellationToken);

        var nullYaml = await File.ReadAllTextAsync(Path.Combine(nullDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        var emptyYaml = await File.ReadAllTextAsync(Path.Combine(emptyDir, "hive-config.yaml"), TestContext.Current.CancellationToken);

        Assert.Equal(
            new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
                .Build()
                .Serialize(withNull),
            nullYaml);
        Assert.Equal(
            new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
                .Build()
                .Serialize(withEmpty),
            emptyYaml);
        Assert.DoesNotContain("available_models", nullYaml);
        Assert.DoesNotContain("sub_agent_models", nullYaml);
        Assert.Contains("available_models:", emptyYaml);
        Assert.Contains("sub_agent_models:", emptyYaml);
    }

    /// <summary>
    /// <see cref="HiveConfigFile.IsConfigured"/> must never reach the persisted YAML — even when
    /// set on the live instance. The snapshot excludes it by construction.
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_IsConfiguredSet_StaysAbsentFromDisk()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "m" },
            IsConfigured = true
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var disk = await ReadWrittenYamlAsync(manager);
        Assert.Equal(
            new SerializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
                .Build()
                .Serialize(config),
            disk);
        Assert.DoesNotContain("is_configured", disk);
    }

    // ── WriteConfigAsync catalog-monitor contention regression ───────────────

    /// <summary>
    /// Reflection handle for <see cref="HiveConfigFile"/>'s private catalog monitor — the same
    /// test-only access pattern used by <c>HiveConfigFileCatalogSafetyTests</c>. Resolved once;
    /// a stale field name fails here at setup rather than silently passing.
    /// </summary>
    private static readonly FieldInfo CatalogLockField = typeof(HiveConfigFile)
        .GetField("_catalogLock", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("HiveConfigFile._catalogLock field not found — test setup is stale.");

    /// <summary>Positive-observation bound for monitor contention (must SUCCEED within it).</summary>
    private static readonly TimeSpan WriteContentionObservationBound = TimeSpan.FromSeconds(30);

    /// <summary>Bound for each bounded thread join attempt, including on the failure path.</summary>
    private static readonly TimeSpan WriteThreadJoinBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bound for the write-Task observation. TIMEOUT-ONLY diagnostic bound: an unresponsive write
    /// fails fast with a <see cref="TimeoutException"/> instead of hanging the run. It can never
    /// fire in the passing flow — a contended write completes promptly once the monitor is released.
    /// <para>
    /// Expiry bounds the OBSERVATION ONLY: <see cref="Task.WaitAsync(TimeSpan)"/> does not stop the
    /// underlying write, so a timeout is explicitly NOT termination and must never authorize file
    /// or directory deletion.
    /// </para>
    /// </summary>
    private static readonly TimeSpan WriteTaskObservationBound = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A dedicated synchronous thread whose body invokes <see cref="ConfigRepoManager.WriteConfigAsync"/>
    /// and hands the returned <see cref="Task"/> back WITHOUT blocking on it — the task is observed
    /// (bounded, timeout-only) only AFTER the monitor is released and the thread has been boundedly
    /// joined, so asynchronous file completion can never masquerade as the monitor wait.
    /// <para>
    /// TERMINATION vs ATTEMPTED DRAINAGE are tracked separately: <see cref="EnsureTerminatedAsync"/>
    /// reports <c>Terminated</c> only when the thread was actually joined AND the write Task ran to
    /// completion (or faulted, or provably never existed). A bound expiry reports NOT terminated.
    /// Faults are CAPTURED and returned rather than thrown mid-cleanup, so cleanup always completes
    /// before anything is surfaced.
    /// </para>
    /// </summary>
    private sealed class WriteCapturedThread
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _done = new(false);
        private readonly ConfigRepoManager _manager;
        private readonly HiveConfigFile _config;
        private Task? _writeTask;
        private Exception? _fault;

        public WriteCapturedThread(string name, ConfigRepoManager manager, HiveConfigFile config)
        {
            _manager = manager;
            _config = config;
            _thread = new Thread(() =>
            {
                try
                {
                    // Capture the Task; deliberately NOT awaited on this thread. Published with a
                    // release write so a failed join followed by a re-read still sees a late publication.
                    Volatile.Write(ref _writeTask, _manager.WriteConfigAsync(_config, TestContext.Current.CancellationToken));
                }
                catch (Exception ex)
                {
                    _fault = ex;
                }
                finally
                {
                    _done.Set();
                }
            })
            {
                IsBackground = true,
                Name = name
            };
        }

        public bool Started { get; private set; }

        /// <summary>Thread name for diagnostics in fault messages.</summary>
        public string Name => _thread.Name!;

        /// <summary>The synchronous body's captured fault, or null when the body completed cleanly.</summary>
        public Exception? BodyFault => _fault;

        public void Start()
        {
            _thread.Start();
            Started = true;
        }

        /// <summary>
        /// Bounded POSITIVE observation that the thread is actually blocked on the catalog
        /// monitor: the thread must be seen in <see cref="System.Threading.ThreadState.WaitSleepJoin"/> — the state
        /// the CLR assigns a thread parked inside <c>Monitor.Enter</c>. A write that completes
        /// without contending fails immediately; an expired deadline fails too.
        /// </summary>
        public string? WaitUntilBlockedOnMonitor(TimeSpan bound)
        {
            var deadline = Environment.TickCount64 + (long)bound.TotalMilliseconds;
            while (Environment.TickCount64 < deadline)
            {
                if (_done.IsSet)
                    return $"Write thread '{_thread.Name}' COMPLETED while the catalog monitor was held — WriteConfigAsync's snapshot read never contended for _catalogLock.";

                if ((_thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0)
                    return null;   // positively observed blocked on the monitor

                Thread.Yield();
            }

            return $"Write thread '{_thread.Name}' was never observed blocked on the catalog monitor within {bound}.";
        }

        /// <summary>
        /// Drives BOTH the invocation thread and the returned write Task to OBSERVED termination
        /// outside the monitor, and reports termination separately from any fault.
        /// <para>
        /// <c>Terminated</c> is <c>true</c> ONLY when the thread was actually joined AND the write
        /// Task completed or faulted (or the body faulted before publishing one, so no async work
        /// exists). It is NEVER set on a join-bound or observation-bound expiry, and never when the
        /// Task was unpublished at a failed join — a failed join is followed by a re-read (late
        /// publication is drained too) and a retried join. Only a <c>true</c> result may authorize
        /// deleting files the write could still touch.
        /// </para>
        /// The Task is observed with the timeout-only <see cref="Task.WaitAsync(TimeSpan)"/> overload,
        /// never a bare unbounded await; a <see cref="TimeoutException"/> is returned as a diagnostic
        /// fault WITHOUT claiming termination, because the observation timing out does not stop the write.
        /// </summary>
        public async Task<(bool Terminated, Exception? Fault)> EnsureTerminatedAsync(
            TimeSpan joinBound, TimeSpan taskBound)
        {
            if (!Started)
                return (true, null);   // nothing ever ran — no thread, no Task, nothing to drain

            var joined = _thread.Join(joinBound);

            // The body publishes _writeTask BEFORE signalling completion, so a successful join makes
            // the publication (or the body fault) definitively visible. After a FAILED join the field
            // may still be unpublished, so whatever is visible is observed and the join is retried.
            var task = Volatile.Read(ref _writeTask);
            var (taskTerminated, fault) = await ObserveTaskAsync(task, taskBound);

            if (!joined)
            {
                // Retry the join; the body may simply have been slow. A Task published AFTER the
                // first failed join is observed here rather than left running.
                joined = _thread.Join(joinBound);
                var late = Volatile.Read(ref _writeTask);
                if (!ReferenceEquals(late, task))
                {
                    var (lateTerminated, lateFault) = await ObserveTaskAsync(late, taskBound);
                    taskTerminated = lateTerminated;
                    fault ??= lateFault;
                }
                else if (joined && late is null)
                {
                    taskTerminated = true;   // joined, nothing was ever published: the body faulted
                }
            }

            return (joined && taskTerminated, fault);
        }

        /// <summary>
        /// Observes one Task under the timeout-only bound. Returns whether it actually terminated
        /// (completed or faulted) plus any fault; a null Task terminated trivially. A bound expiry
        /// returns <c>false</c> — the write is still running.
        /// </summary>
        private async Task<(bool Terminated, Exception? Fault)> ObserveTaskAsync(Task? task, TimeSpan bound)
        {
            if (task is null)
                return (true, null);

            try
            {
                await task.WaitAsync(bound);
                return (true, null);
            }
            catch (TimeoutException)
            {
                return (false, new TimeoutException(
                    $"Write Task '{_thread.Name}' did not complete within {bound}. WaitAsync bounds the " +
                    "OBSERVATION only — the write is still running, so nothing it may touch was deleted."));
            }
            catch (Exception ex)
            {
                // The write Task itself faulted: it DID terminate; the fault is surfaced after cleanup.
                return (true, ex);
            }
        }
    }

    /// <summary>
    /// DETERMINISTIC contention regression: the snapshot read inside
    /// <see cref="ConfigRepoManager.WriteConfigAsync"/> must participate in the instance's catalog
    /// monitor. Evidence chain:
    /// (1) fixtures are created and warmed BEFORE the contention window;
    /// (2) the test thread enters the catalog monitor;
    /// (3) a dedicated synchronous thread starts <see cref="ConfigRepoManager.WriteConfigAsync"/>
    ///     and is POSITIVELY observed BLOCKED on that monitor (a snapshot-free write completes
    ///     instead and fails the observation immediately — no timed non-completion proxy);
    /// (4) while the write is provably parked, a synchronized catalog update commits a
    ///     distinguishing model entry BEHIND it;
    /// (5) the monitor is released BEFORE any await, then the invocation thread and the returned
    ///     write Task are driven to OBSERVED termination outside the monitor (bounded join plus the
    ///     timeout-only <c>WaitAsync</c> observation, with a re-read for late publication) — on
    ///     success AND on every failure path after startup — and the persisted file must carry the
    ///     distinguishing values, only possible if the snapshot was captured after acquiring the
    ///     monitor.
    /// <para>
    /// Deletion ordering: the worker writes into its OWN directory (never the shared fixture
    /// directory that Dispose deletes unconditionally), and that directory is removed ONLY after
    /// termination was positively observed. A bound expiry is an OBSERVATION timeout, not
    /// termination — it fails the test and deliberately leaves the directory in place.
    /// Fault marshalling: the primary (step/assertion) exception is captured BEFORE cleanup and is
    /// emitted afterwards as the authoritative fault, always accompanied by every cleanup fault
    /// (join/observation timeouts, body faults, write-Task faults, helper faults, and any
    /// directory-deletion failure) — none is discarded. Emission happens LAST, after the guarded
    /// deletion attempt, so a failing <c>Directory.Delete</c> can never replace a pending fault.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WriteConfigAsync_SnapshotRead_WaitsForHeldCatalogMonitor()
    {
        // The contention worker gets its OWN directory, deliberately OUTSIDE the shared fixture
        // directory: the fixture's Dispose deletes `_tempDir` unconditionally, which must never be
        // able to race a write that has not been observed as terminated. This directory is deleted
        // by this test ONLY after positive termination (see the final finally).
        var contentionDir = Path.Combine(
            Path.GetTempPath(), $"copilothive-cfgtest-contention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(contentionDir);

        // Fixtures created and warmed BEFORE contention: one write settles the file/cache path.
        var warmConfig = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "warm", ContextWindow = 1 }]
            }
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", contentionDir);
        await manager.WriteConfigAsync(warmConfig, TestContext.Current.CancellationToken);
        _ = await manager.LoadConfigAsync(TestContext.Current.CancellationToken);

        // The live config the contending write will serialize: WITHOUT the distinguishing entry.
        var liveConfig = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "pre", ContextWindow = 10 }]
            }
        };

        // The distinguishing synchronized mutation committed BEHIND the blocked write, directly
        // on the live config object whose snapshot the write will capture.
        var writerSucceeded = false;

        var writeThread = new WriteCapturedThread("writeconfig-catalog-monitor-writer", manager, liveConfig);

        string? contentionFailure = null;
        var committed = false;
        var monitorEntered = false;

        // The PRIMARY exception (any step failure inside the contention region, e.g. the
        // distinguishing update throwing) is captured BEFORE cleanup runs, so cleanup always
        // executes and the primary is still emitted afterwards as the authoritative fault —
        // together with EVERY cleanup fault, none of which is ever discarded.
        Exception? primaryFault = null;
        var cleanupFaults = new List<Exception>();

        try
        {
            var monitor = CatalogLockField.GetValue(liveConfig)!;
            Monitor.Enter(monitor);
            monitorEntered = true;
            writeThread.Start();

            contentionFailure = writeThread.WaitUntilBlockedOnMonitor(WriteContentionObservationBound);
            if (contentionFailure is null)
            {
                writerSucceeded = liveConfig.TryAddAvailableModel(
                    new AvailableModelRequest("post", 99, null, null));
                committed = true;
            }
        }
        catch (Exception ex)
        {
            primaryFault = ex;
        }
        finally
        {
            // ALWAYS release the monitor — and release it BEFORE any await, so the monitor is
            // never held across the asynchronous termination wait below.
            if (monitorEntered && Monitor.IsEntered(CatalogLockField.GetValue(liveConfig)!))
                Monitor.Exit(CatalogLockField.GetValue(liveConfig)!);
        }

        // Cleanup OUTSIDE the monitor, on the success path AND on every failure path after
        // startup: drive the invocation thread and the returned write Task to OBSERVED
        // termination. `terminated` is the only authority for deleting anything the write could
        // still touch — a bound expiry is an observation timeout, NOT termination.
        var terminated = false;
        try
        {
            (terminated, var terminationFault) = await writeThread.EnsureTerminatedAsync(
                WriteThreadJoinBound, WriteTaskObservationBound);
            if (terminationFault is not null)
                cleanupFaults.Add(terminationFault);
        }
        catch (Exception ex)
        {
            // Never discard a cleanup fault, even one thrown by the termination helper itself.
            cleanupFaults.Add(ex);
        }

        if (writeThread.BodyFault is not null)
            cleanupFaults.Add(writeThread.BodyFault);

        if (!terminated)
            cleanupFaults.Add(new TimeoutException(
                $"Write worker '{writeThread.Name}' was NOT observed terminated within its bounds " +
                $"(join {WriteThreadJoinBound}, task observation {WriteTaskObservationBound}). The test " +
                $"fails WITHOUT deleting '{contentionDir}', which the still-running write may touch."));

        // Assertions and disk verification run only when no worker fault was accumulated (an
        // accumulated fault is authoritative and would make them noise). Any failure here is
        // CAPTURED, never thrown, so the guarded deletion below still runs BEFORE emission —
        // a delete failure can therefore never replace this fault.
        if (primaryFault is null && cleanupFaults.Count == 0)
        {
            try
            {
                Assert.True(contentionFailure is null, contentionFailure);
                Assert.True(committed, "The distinguishing catalog update was never committed behind the blocked write.");
                Assert.True(writerSucceeded, "TryAddAvailableModel behind the blocked write did not succeed.");

                // Verify the distinguishing values FROM DISK (not the manager cache): the write must
                // have snapshotted the catalog AFTER acquiring the monitor, so the committed entry and
                // its values are in the persisted YAML.
                var disk = await File.ReadAllTextAsync(
                    Path.Combine(contentionDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
                var parsed = ConfigRepoManager.ParseConfig(disk);
                var available = parsed.Models!.AvailableModels!;
                Assert.Equal(2, available.Count);
                Assert.Equal("pre", available[0].Name);
                Assert.Equal(10, available[0].ContextWindow);
                Assert.Equal("post", available[1].Name);
                Assert.Equal(99, available[1].ContextWindow);
            }
            catch (Exception ex)
            {
                // The assertion/read failure is the authoritative primary fault.
                primaryFault = ex;
            }
        }

        // GUARDED deletion, performed BEFORE any fault is emitted. The contention worker writes
        // into its OWN directory (never the shared fixture directory that Dispose deletes
        // unconditionally), and that directory is deleted ONLY after termination was positively
        // observed. On an unconfirmed termination it is deliberately left in place: no deletion may
        // race a write that is still running. A deletion failure is COLLECTED as a cleanup fault —
        // never swallowed, and never allowed to replace the pending authoritative fault.
        try
        {
            if (terminated && Directory.Exists(contentionDir))
                Directory.Delete(contentionDir, recursive: true);
        }
        catch (Exception ex)
        {
            cleanupFaults.Add(ex);
        }

        // Emission LAST: the primary fault stays authoritative and is always accompanied by every
        // cleanup fault (termination timeout, body fault, write-Task fault, helper fault, and any
        // directory-deletion failure).
        if (primaryFault is not null && cleanupFaults.Count == 0)
            ExceptionDispatchInfo.Capture(primaryFault).Throw();

        if (primaryFault is not null || cleanupFaults.Count > 0)
        {
            var all = new List<Exception>();
            if (primaryFault is not null)
                all.Add(primaryFault);
            all.AddRange(cleanupFaults);
            throw new AggregateException(
                "Contention test failed. Authoritative (first) fault: " +
                (primaryFault?.Message ?? cleanupFaults[0].Message),
                all);
        }
    }

    // ── Release config parsing ───────────────────────────────────────────────

    [Fact]
    public void ParseConfig_ReleaseSection_ParsesFields()
    {
        const string yaml = """
            version: "1.0"
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
                default_branch: main
                release:
                  merge_to: main
                  tag_branch: main
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        var repo = Assert.Single(config.Repositories);
        Assert.NotNull(repo.Release);
        Assert.Equal("main", repo.Release!.MergeTo);
        Assert.Equal("main", repo.Release!.TagBranch);
    }

    [Fact]
    public void ParseConfig_NoReleaseSection_ReleaseIsNull()
    {
        const string yaml = """
            version: "1.0"
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
                default_branch: main
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        var repo = Assert.Single(config.Repositories);
        Assert.Null(repo.Release);
    }

    [Fact]
    public void ParseConfig_EmptyReleaseObject_NormalizesToNull()
    {
        const string yaml = """
            version: "1.0"
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
                default_branch: main
                release: {}
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        var repo = Assert.Single(config.Repositories);
        Assert.Null(repo.Release);
    }

    [Fact]
    public void ParseConfig_ReleaseWithWhitespaceOnly_NormalizesToNull()
    {
        const string yaml = """
            version: "1.0"
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
                default_branch: main
                release:
                  merge_to: ""
                  tag_branch: "  "
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        var repo = Assert.Single(config.Repositories);
        Assert.Null(repo.Release);
    }

    [Fact]
    public void ParseConfig_ReleaseWithOnlyMergeTo_PreservesRelease()
    {
        const string yaml = """
            version: "1.0"
            repositories:
              - name: my-app
                url: https://github.com/org/my-app.git
                default_branch: main
                release:
                  merge_to: main
            """;

        var config = ConfigRepoManager.ParseConfig(yaml);

        var repo = Assert.Single(config.Repositories);
        Assert.NotNull(repo.Release);
        Assert.Equal("main", repo.Release!.MergeTo);
        Assert.Null(repo.Release!.TagBranch);
    }

    [Fact]
    public async Task WriteConfigAsync_SerializesReleaseWithSnakeCaseKeys()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "test-model" },
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-app",
                    Url = "https://github.com/org/my-app.git",
                    DefaultBranch = "main",
                    Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "main" }
                }
            ]
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("release:", yaml);
        Assert.Contains("merge_to: main", yaml);
        Assert.Contains("tag_branch: main", yaml);
    }

    [Fact]
    public async Task WriteConfigAsync_NullRelease_OmitsReleaseSection()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "test-model" },
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-app",
                    Url = "https://github.com/org/my-app.git",
                    DefaultBranch = "main"
                }
            ]
        };
        var manager = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);

        await manager.WriteConfigAsync(config, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("release:", yaml);
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    private async Task<ConfigRepoManager> CreateManagerWithConfigAsync(string yaml)
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "hive-config.yaml"), yaml, TestContext.Current.CancellationToken);
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        await manager.LoadConfigAsync(TestContext.Current.CancellationToken);
        return manager;
    }

    // ── Semaphore serialization ───────────────────────────────────────────────

    [Fact]
    public async Task SyncRepoAsync_ConcurrentCalls_AreSerializedBySemaphore()
    {
        // Arrange — grab the _gitLock semaphore via reflection and hold it.
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var gitLock = GetGitLock(manager);

        // Pre-acquire the semaphore to block any git operation.
        await gitLock.WaitAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Start a SyncRepoAsync that will block waiting for the semaphore.
        var blockingTask = Task.Run(async () =>
        {
            started.TrySetResult(true);
            // This will block on WaitAsync until we release the semaphore below.
            await manager.SyncRepoAsync(ct);
        }, ct);

        // Wait until the task has at least started.
        await started.Task;

        // The task should NOT complete while the semaphore is held.
        var completedEarly = await Task.WhenAny(blockingTask, Task.Delay(100, ct)) == blockingTask;
        Assert.False(completedEarly, "SyncRepoAsync should be blocked while the semaphore is held");

        // Release the semaphore — note: the call will fail (no .git, no real remote),
        // but it will at least proceed past the lock.
        gitLock.Release();

        // The task should now run and eventually throw (no real git repo), but it must
        // have been unblocked by the semaphore release.
        await Assert.ThrowsAsync<InvalidOperationException>(() => blockingTask);
    }

    [Fact]
    public async Task CommitAllChangesAsync_ConcurrentCalls_AreSerializedBySemaphore()
    {
        // Arrange — grab the _gitLock semaphore via reflection and hold it.
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var gitLock = GetGitLock(manager);

        await gitLock.WaitAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingTask = Task.Run(async () =>
        {
            started.TrySetResult(true);
            await manager.CommitAllChangesAsync("test commit", ct);
        }, ct);

        await started.Task;

        var completedEarly = await Task.WhenAny(blockingTask, Task.Delay(100, ct)) == blockingTask;
        Assert.False(completedEarly, "CommitAllChangesAsync should be blocked while the semaphore is held");

        gitLock.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(() => blockingTask);
    }

    [Fact]
    public async Task CommitFileAsync_ConcurrentCalls_AreSerializedBySemaphore()
    {
        // Arrange — grab the _gitLock semaphore via reflection and hold it.
        var manager = new ConfigRepoManager("https://example.com/config.git", _tempDir);
        var gitLock = GetGitLock(manager);

        await gitLock.WaitAsync(TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var blockingTask = Task.Run(async () =>
        {
            started.TrySetResult(true);
            await manager.CommitFileAsync("config.txt", "update config", ct);
        }, ct);

        await started.Task;

        var completedEarly = await Task.WhenAny(blockingTask, Task.Delay(100, ct)) == blockingTask;
        Assert.False(completedEarly, "CommitFileAsync should be blocked while the semaphore is held");

        gitLock.Release();

        await Assert.ThrowsAsync<InvalidOperationException>(() => blockingTask);
    }

    // ── Merge conflict abort ──────────────────────────────────────────────────

    [Fact]
    public async Task SyncRepoAsync_WhenPullFails_AttemptsMergeAbortAndRethrows()
    {
        // Arrange — set up a local bare remote and two clones that create a conflict.
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test"]);

            // Write initial file and push.
            var filePath = Path.Combine(clone1Dir, "conflict.txt");
            await File.WriteAllTextAsync(filePath, "line1\n", TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "conflict.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare, modify conflict.txt, commit but DON'T push yet.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "conflict.txt"), "clone2 change\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone2Dir, ["add", "conflict.txt"]);
            await RunGitCommandAsync(clone2Dir, ["commit", "-m", "clone2 local commit"]);

            // Clone1: push a conflicting change to the same file.
            await File.WriteAllTextAsync(filePath, "clone1 different change\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "conflict.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "clone1 conflict"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Now clone2 has a local commit that conflicts with the remote.
            // Set merge strategy to always merge (not fast-forward) so a conflict occurs.
            await RunGitCommandAsync(clone2Dir, ["config", "pull.rebase", "false"]);

            // Act — SyncRepoAsync on clone2 should fail (merge conflict) and
            // attempt git merge --abort before rethrowing.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.SyncRepoAsync(TestContext.Current.CancellationToken));

            // Assert — the exception comes from the pull failure (not abort).
            Assert.Contains("git exited with code", ex.Message);

            // After TryAbortMergeAsync the repo should be in a clean non-merging state.
            // git status should not show "MERGING".
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.DoesNotContain("MERGING", statusOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // ── Conflict recovery (PushWithConflictRecoveryAsync via CommitFileAsync) ──

    [Fact]
    public async Task CommitFileAsync_WhenPullConflicts_RebasesAndPushes()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "base content\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            // Clone1: push a conflicting change to the same file (different line, rebase-friendly).
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "base content\nremote line\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote change"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: write a local change to a DIFFERENT line so rebase can succeed.
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "config.txt"), "local content\nbase content\n",
                TestContext.Current.CancellationToken);

            // Set pull.rebase false so plain pull attempts a merge (which will conflict).
            await RunGitCommandAsync(clone2Dir, ["config", "pull.rebase", "false"]);

            // Act — CommitFileAsync should detect the conflict, abort merge, reset,
            // rebase onto remote, and push. No exception should be thrown.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            await manager.CommitFileAsync("config.txt", "local change", TestContext.Current.CancellationToken);

            // Assert — the remote now has both commits (rebase succeeded).
            // Clone the remote to a fresh clone to verify state.
            var verifyDir = Path.Combine(Path.GetTempPath(), $"cfgtest-verify-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(verifyDir);
                await RunGitCommandAsync(Path.GetDirectoryName(verifyDir)!,
                    ["clone", bareDir, Path.GetFileName(verifyDir)]);
                var remoteContent = await File.ReadAllTextAsync(
                    Path.Combine(verifyDir, "config.txt"), TestContext.Current.CancellationToken);

                // After a successful rebase, both lines should be present since they
                // modified different parts of the file.
                Assert.Contains("base content", remoteContent);
                Assert.Contains("local content", remoteContent);
                Assert.Contains("remote line", remoteContent);

                // Verify both commits exist on the remote.
                var (logOutput, _) = await RunGitCommandRawAsync(verifyDir, ["log", "--oneline"]);
                Assert.Contains("local change", logOutput);
                Assert.Contains("remote change", logOutput);
            }
            finally
            {
                if (Directory.Exists(verifyDir))
                    try { Directory.Delete(verifyDir, recursive: true); } catch { }
            }

            // Clone2 should be in a clean state (not mid-merge or mid-rebase).
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.DoesNotContain("MERGING", statusOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("REBASE", statusOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("working tree clean", statusOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CommitFileAsync_WhenPullAndRebaseBothFail_ResetsAndPushes()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "line1\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            // Clone1: push a conflicting change to the SAME line.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "remote-only-content\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote change"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: write a local change to the SAME line (will conflict on rebase too).
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "config.txt"), "local-only-content\n",
                TestContext.Current.CancellationToken);

            // Set pull.rebase false so plain pull attempts a merge (which will conflict).
            await RunGitCommandAsync(clone2Dir, ["config", "pull.rebase", "false"]);

            // Act — CommitFileAsync should: pull fails → abort merge → reset →
            // pull --rebase fails → abort rebase → reset hard → push local commit.
            // The push will also fail (non-fast-forward) since the local commit diverged.
            // The exception from git push propagates, but the repo must be in a clean state.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.CommitFileAsync("config.txt", "local change", TestContext.Current.CancellationToken));

            // The exception should come from the push failure (not a merge/rebase conflict).
            Assert.Contains("git exited with code", ex.Message);

            // The repo should be in a clean state — not mid-merge or mid-rebase.
            // This is the key assertion: even though the push failed, the recovery
            // logic (abort merge, reset hard, abort rebase, reset hard) ensured the
            // repo is not stuck in a conflicted state.
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.DoesNotContain("MERGING", statusOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("REBASE", statusOutput, StringComparison.OrdinalIgnoreCase);

            // Verify the local file still contains the local commit's content
            // (reset --hard HEAD preserved the local commit).
            var localContent = await File.ReadAllTextAsync(
                Path.Combine(clone2Dir, "config.txt"), TestContext.Current.CancellationToken);
            Assert.Contains("local-only-content", localContent);

            // Verify the local commit exists in the local log.
            var (logOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["log", "--oneline"]);
            Assert.Contains("local change", logOutput);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CommitAllChangesAsync_WhenPullConflicts_RebasesAndPushes()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "data.txt"), "base\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "data.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            // Clone1: push a conflicting change to the same file (different line for rebase success).
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "data.txt"), "base\nremote line\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "data.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote change"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: write a local change to a DIFFERENT line so rebase can succeed.
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "data.txt"), "local line\nbase\n",
                TestContext.Current.CancellationToken);

            // Set pull.rebase false so plain pull attempts a merge (which will conflict).
            await RunGitCommandAsync(clone2Dir, ["config", "pull.rebase", "false"]);

            // Act — CommitAllChangesAsync should detect the conflict, abort merge,
            // reset, rebase onto remote, and push. No exception should be thrown.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            await manager.CommitAllChangesAsync("local change via commit-all", TestContext.Current.CancellationToken);

            // Assert — the remote now has both commits (rebase succeeded).
            var verifyDir = Path.Combine(Path.GetTempPath(), $"cfgtest-verify-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(verifyDir);
                await RunGitCommandAsync(Path.GetDirectoryName(verifyDir)!,
                    ["clone", bareDir, Path.GetFileName(verifyDir)]);
                var remoteContent = await File.ReadAllTextAsync(
                    Path.Combine(verifyDir, "data.txt"), TestContext.Current.CancellationToken);

                // After a successful rebase, both lines should be present.
                Assert.Contains("base", remoteContent);
                Assert.Contains("local line", remoteContent);
                Assert.Contains("remote line", remoteContent);

                // Verify the local commit exists on the remote.
                var (logOutput, _) = await RunGitCommandRawAsync(verifyDir, ["log", "--oneline"]);
                Assert.Contains("local change via commit-all", logOutput);
                Assert.Contains("remote change", logOutput);
            }
            finally
            {
                if (Directory.Exists(verifyDir))
                    try { Directory.Delete(verifyDir, recursive: true); } catch { }
            }

            // Clone2 should be in a clean state.
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.DoesNotContain("MERGING", statusOutput, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("REBASE", statusOutput, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── ResetToRemoteAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task ResetToRemoteAsync_ResetsToRemoteState()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "remote content\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote commit"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare, then make local commits.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            // Make a local commit that diverges from remote.
            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "config.txt"), "local content\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone2Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone2Dir, ["commit", "-m", "local commit"]);

            // Act — ResetToRemoteAsync should discard local commits and match remote.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            await manager.ResetToRemoteAsync(TestContext.Current.CancellationToken);

            // Assert — local file matches remote content.
            var localContent = await File.ReadAllTextAsync(
                Path.Combine(clone2Dir, "config.txt"), TestContext.Current.CancellationToken);
            Assert.Equal("remote content\n", localContent);

            // Git status should be clean (no local changes, working tree clean).
            var (statusOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["status"]);
            Assert.Contains("working tree clean", statusOutput, StringComparison.OrdinalIgnoreCase);

            // Local log should match remote log — no "local commit".
            var (logOutput, _) = await RunGitCommandRawAsync(clone2Dir, ["log", "--oneline"]);
            Assert.Contains("remote commit", logOutput);
            Assert.DoesNotContain("local commit", logOutput);
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ResetToRemoteAsync_AbortsActiveMerge()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var clone1Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone1-{Guid.NewGuid():N}");
        var clone2Dir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone2-{Guid.NewGuid():N}");

        try
        {
            // Create bare repo and initial commit in clone1.
            Directory.CreateDirectory(bareDir);
            await RunGitCommandAsync(bareDir, ["init", "--bare"]);
            Directory.CreateDirectory(clone1Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone1Dir)!,
                ["clone", bareDir, Path.GetFileName(clone1Dir)]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.email", "test@test.com"]);
            await RunGitCommandAsync(clone1Dir, ["config", "user.name", "Test1"]);

            // Write initial file and push.
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "base\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "initial"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: clone from bare (at "initial" commit), then make a local conflicting commit.
            Directory.CreateDirectory(clone2Dir);
            await RunGitCommandAsync(Path.GetDirectoryName(clone2Dir)!,
                ["clone", bareDir, Path.GetFileName(clone2Dir)]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.email", "test2@test.com"]);
            await RunGitCommandAsync(clone2Dir, ["config", "user.name", "Test2"]);

            await File.WriteAllTextAsync(Path.Combine(clone2Dir, "config.txt"), "local change\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone2Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone2Dir, ["commit", "-m", "local change"]);

            // Clone1: push a conflicting change to the SAME line (diverges from clone2).
            await File.WriteAllTextAsync(Path.Combine(clone1Dir, "config.txt"), "remote change\n",
                TestContext.Current.CancellationToken);
            await RunGitCommandAsync(clone1Dir, ["add", "config.txt"]);
            await RunGitCommandAsync(clone1Dir, ["commit", "-m", "remote change"]);
            await RunGitCommandAsync(clone1Dir, ["push", "origin", "HEAD"]);

            // Clone2: fetch the remote change, then start a merge that will conflict.
            await RunGitCommandAsync(clone2Dir, ["fetch", "origin"]);
            // merge will fail with a conflict — this puts the repo in a merging state.
            var (_, _) = await RunGitCommandRawAsync(clone2Dir, ["merge", "origin/HEAD"]);
            // Verify merge is in progress (MERGE_HEAD exists) and config.txt is unmerged.
            var (mergeHead, _) = await RunGitCommandRawAsync(clone2Dir, ["rev-parse", "--verify", "MERGE_HEAD"]);
            Assert.False(string.IsNullOrWhiteSpace(mergeHead), "Expected MERGE_HEAD to exist after conflicting merge");

            var (unmergedFiles, _) = await RunGitCommandRawAsync(clone2Dir, ["diff", "--name-only", "--diff-filter=U"]);
            Assert.Contains("config.txt", unmergedFiles.Trim(), StringComparison.OrdinalIgnoreCase);

            // Act — ResetToRemoteAsync should abort the merge and reset to remote.
            var manager = new ConfigRepoManager(bareDir, clone2Dir);
            await manager.ResetToRemoteAsync(TestContext.Current.CancellationToken);

            // Assert — repo is no longer in a merging state.
            var (mergeHeadAfter, _) = await RunGitCommandRawAsync(clone2Dir, ["rev-parse", "--verify", "MERGE_HEAD"]);
            Assert.True(string.IsNullOrWhiteSpace(mergeHeadAfter), "MERGE_HEAD should not exist after reset");

            var (unmergedAfter, _) = await RunGitCommandRawAsync(clone2Dir, ["diff", "--name-only", "--diff-filter=U"]);
            Assert.Equal("", unmergedAfter.Trim());

            // The local content should now match the remote.
            var localContent = await File.ReadAllTextAsync(
                Path.Combine(clone2Dir, "config.txt"), TestContext.Current.CancellationToken);
            Assert.Equal("remote change\n", localContent);

            // Status should be clean.
            var (porcelainAfter, _) = await RunGitCommandRawAsync(clone2Dir, ["status", "--porcelain"]);
            Assert.Equal("", porcelainAfter.Trim());
        }
        finally
        {
            foreach (var dir in new[] { bareDir, clone1Dir, clone2Dir })
                if (Directory.Exists(dir))
                    try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ── PushOnlyAsync (no-diff fast path) ─────────────────────────────────────

    /// <summary>
    /// Creates a bare remote plus a clone that tracks it, with one initial commit.
    /// Returns (bareDir, cloneDir).
    /// </summary>
    private static async Task<(string BareDir, string CloneDir)> CreateRemoteAndCloneAsync()
    {
        var bareDir = Path.Combine(Path.GetTempPath(), $"cfgtest-bare-{Guid.NewGuid():N}");
        var seedDir = Path.Combine(Path.GetTempPath(), $"cfgtest-seed-{Guid.NewGuid():N}");
        var cloneDir = Path.Combine(Path.GetTempPath(), $"cfgtest-clone-{Guid.NewGuid():N}");

        Directory.CreateDirectory(bareDir);
        await RunGitCommandAsync(bareDir, ["init", "--bare"]);

        Directory.CreateDirectory(seedDir);
        await RunGitCommandAsync(Path.GetDirectoryName(seedDir)!, ["clone", bareDir, Path.GetFileName(seedDir)]);
        await RunGitCommandAsync(seedDir, ["config", "user.email", "seed@test.com"]);
        await RunGitCommandAsync(seedDir, ["config", "user.name", "Seed"]);
        await File.WriteAllTextAsync(Path.Combine(seedDir, "tracked.txt"), "tracked\n");
        await File.WriteAllTextAsync(Path.Combine(seedDir, "other.txt"), "other\n");
        await RunGitCommandAsync(seedDir, ["add", "."]);
        await RunGitCommandAsync(seedDir, ["commit", "-m", "initial"]);
        await RunGitCommandAsync(seedDir, ["push", "origin", "HEAD"]);
        try { Directory.Delete(seedDir, recursive: true); } catch { /* best-effort */ }

        Directory.CreateDirectory(cloneDir);
        await RunGitCommandAsync(Path.GetDirectoryName(cloneDir)!, ["clone", bareDir, Path.GetFileName(cloneDir)]);
        await RunGitCommandAsync(cloneDir, ["config", "user.email", "clone@test.com"]);
        await RunGitCommandAsync(cloneDir, ["config", "user.name", "Clone"]);

        return (bareDir, cloneDir);
    }

    /// <summary>Creates a standalone git repo with one commit and NO remote configured.</summary>
    private static async Task<string> CreateRepoWithoutRemoteAsync()
    {
        var repoDir = Path.Combine(Path.GetTempPath(), $"cfgtest-noremote-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);
        await RunGitCommandAsync(repoDir, ["init"]);
        await RunGitCommandAsync(repoDir, ["config", "user.email", "local@test.com"]);
        await RunGitCommandAsync(repoDir, ["config", "user.name", "Local"]);
        await File.WriteAllTextAsync(Path.Combine(repoDir, "tracked.txt"), "tracked\n");
        await File.WriteAllTextAsync(Path.Combine(repoDir, "other.txt"), "other\n");
        await RunGitCommandAsync(repoDir, ["add", "."]);
        await RunGitCommandAsync(repoDir, ["commit", "-m", "initial"]);
        return repoDir;
    }

    private static void CleanupDirs(params string[] dirs)
    {
        foreach (var dir in dirs)
            if (Directory.Exists(dir))
                try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task DeleteFileAsync_FileAlreadyRemovedFromIndex_DoesNotThrowAndStillPushes()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // Remove the file and commit the removal — the index no longer knows about it.
            File.Delete(Path.Combine(cloneDir, "tracked.txt"));
            await RunGitCommandAsync(cloneDir, ["rm", "--cached", "tracked.txt"]);
            await RunGitCommandAsync(cloneDir, ["commit", "-m", "already removed"]);

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            // Act — a retry of the same deletion must not fail thanks to --ignore-unmatch.
            await manager.DeleteFileAsync("tracked.txt", "retry deletion", ct);

            // Assert — the pending local commit was pushed by PushOnlyAsync.
            var (remoteLog, _) = await RunGitCommandRawAsync(bareDir, ["log", "--oneline"]);
            Assert.Contains("already removed", remoteLog);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task DeleteFileAsync_NothingToCommit_PushesPendingCommits()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // A local commit that has not yet been pushed.
            await File.WriteAllTextAsync(Path.Combine(cloneDir, "other.txt"), "changed\n", ct);
            await RunGitCommandAsync(cloneDir, ["add", "other.txt"]);
            await RunGitCommandAsync(cloneDir, ["commit", "-m", "unpushed local commit"]);

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            // Act — nothing staged for "missing.txt" → no diff → PushOnlyAsync.
            await manager.DeleteFileAsync("missing.txt", "no-op deletion", ct);

            // Assert — the push happened.
            var (remoteLog, _) = await RunGitCommandRawAsync(bareDir, ["log", "--oneline"]);
            Assert.Contains("unpushed local commit", remoteLog);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task DeleteFileAsync_PushFails_PropagatesAndPreservesWorkingTree()
    {
        var repoDir = await CreateRepoWithoutRemoteAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // An unrelated unstaged change that must survive a failed push.
            await File.WriteAllTextAsync(Path.Combine(repoDir, "other.txt"), "unstaged edit\n", ct);

            var manager = new ConfigRepoManager("https://example.com/config.git", repoDir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.DeleteFileAsync("missing.txt", "no-op deletion", ct));
            Assert.Contains("git exited with code", ex.Message);

            // No reset --hard was performed — the unstaged change is intact.
            var content = await File.ReadAllTextAsync(Path.Combine(repoDir, "other.txt"), ct);
            Assert.Equal("unstaged edit\n", content);
        }
        finally
        {
            CleanupDirs(repoDir);
        }
    }

    [Fact]
    public async Task CommitFileAsync_NothingToCommit_PushesPendingCommits()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            await File.WriteAllTextAsync(Path.Combine(cloneDir, "other.txt"), "changed\n", ct);
            await RunGitCommandAsync(cloneDir, ["add", "other.txt"]);
            await RunGitCommandAsync(cloneDir, ["commit", "-m", "unpushed commit for commitfile"]);

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            // Act — tracked.txt is unchanged → nothing staged → PushOnlyAsync.
            await manager.CommitFileAsync("tracked.txt", "no-op commit", ct);

            var (remoteLog, _) = await RunGitCommandRawAsync(bareDir, ["log", "--oneline"]);
            Assert.Contains("unpushed commit for commitfile", remoteLog);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task CommitFileAsync_NothingToCommitAndPushFails_Propagates()
    {
        var repoDir = await CreateRepoWithoutRemoteAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            var manager = new ConfigRepoManager("https://example.com/config.git", repoDir);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.CommitFileAsync("tracked.txt", "no-op commit", ct));
            Assert.Contains("git exited with code", ex.Message);
        }
        finally
        {
            CleanupDirs(repoDir);
        }
    }

    [Fact]
    public async Task DeleteAndCommitFileAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => manager.DeleteFileAsync("missing.txt", "no-op deletion", cts.Token));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => manager.CommitFileAsync("tracked.txt", "no-op commit", cts.Token));
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task CommitFileAsync_NoDiffPushOnly_DoesNotDiscardUnrelatedWorkingTreeChanges()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // Unrelated unstaged change.
            await File.WriteAllTextAsync(Path.Combine(cloneDir, "other.txt"), "local scratch\n", ct);

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            // Act — tracked.txt is unchanged → no diff → PushOnlyAsync (non-destructive).
            await manager.CommitFileAsync("tracked.txt", "no-op commit", ct);

            // Assert — the unrelated change survived.
            var content = await File.ReadAllTextAsync(Path.Combine(cloneDir, "other.txt"), ct);
            Assert.Equal("local scratch\n", content);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    // ── DeleteFilesAsync (batch delete, single commit) ────────────────────────

    [Fact]
    public async Task DeleteFileAsync_TrackedFile_CommitsAndPushesTheRemoval()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // The caller removes the file from the working tree first.
            File.Delete(Path.Combine(cloneDir, "tracked.txt"));

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            await manager.DeleteFileAsync("tracked.txt", "single deletion", ct);

            // The removal was committed locally…
            var (tracked, _) = await RunGitCommandRawAsync(cloneDir, ["ls-files"]);
            Assert.DoesNotContain("tracked.txt", tracked, StringComparison.Ordinal);
            Assert.Contains("other.txt", tracked, StringComparison.Ordinal);

            // …and pushed to the remote.
            var (remoteLog, _) = await RunGitCommandRawAsync(bareDir, ["log", "--oneline"]);
            Assert.Contains("single deletion", remoteLog);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task DeleteFilesAsync_NullPaths_ThrowsArgumentNullException()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var manager = new ConfigRepoManager(bareDir, cloneDir);

            await Assert.ThrowsAsync<ArgumentNullException>(
                () => manager.DeleteFilesAsync(null!, "batch deletion", TestContext.Current.CancellationToken));
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task DeleteFilesAsync_EmptyPaths_ReturnsWithoutCommittingOrPushing()
    {
        // A repo without a remote makes any push fail — the early return must avoid git entirely.
        var repoDir = await CreateRepoWithoutRemoteAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;
            var (logBefore, _) = await RunGitCommandRawAsync(repoDir, ["rev-list", "--count", "HEAD"]);

            var manager = new ConfigRepoManager("https://example.com/config.git", repoDir);

            // Act — an empty batch is a no-op; it must not throw despite the missing remote.
            await manager.DeleteFilesAsync([], "batch deletion", ct);

            // Assert — no commit was created and the working tree is untouched.
            var (logAfter, _) = await RunGitCommandRawAsync(repoDir, ["rev-list", "--count", "HEAD"]);
            Assert.Equal(logBefore.Trim(), logAfter.Trim());
            Assert.True(File.Exists(Path.Combine(repoDir, "tracked.txt")));
            Assert.True(File.Exists(Path.Combine(repoDir, "other.txt")));
        }
        finally
        {
            CleanupDirs(repoDir);
        }
    }

    [Fact]
    public async Task DeleteFilesAsync_NothingToCommit_PushesPendingCommits()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // A local commit that has not yet been pushed.
            await File.WriteAllTextAsync(Path.Combine(cloneDir, "other.txt"), "changed\n", ct);
            await RunGitCommandAsync(cloneDir, ["add", "other.txt"]);
            await RunGitCommandAsync(cloneDir, ["commit", "-m", "unpushed batch local commit"]);

            var logBefore = (await RunGitCommandRawAsync(cloneDir, ["rev-list", "--count", "HEAD"])).output.Trim();

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            // Act — none of the paths are tracked → nothing staged → PushOnlyAsync.
            await manager.DeleteFilesAsync(["missing-a.txt", "missing-b.txt"], "no-op batch deletion", ct);

            // Assert — the pending commit was pushed and no new commit was created.
            var (remoteLog, _) = await RunGitCommandRawAsync(bareDir, ["log", "--oneline"]);
            Assert.Contains("unpushed batch local commit", remoteLog);
            var logAfter = (await RunGitCommandRawAsync(cloneDir, ["rev-list", "--count", "HEAD"])).output.Trim();
            Assert.Equal(logBefore, logAfter);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task DeleteFilesAsync_MultipleTrackedFiles_RemovesAllInASingleCommit()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            var ct = TestContext.Current.CancellationToken;

            // The caller removes the files from the working tree first.
            File.Delete(Path.Combine(cloneDir, "tracked.txt"));
            File.Delete(Path.Combine(cloneDir, "other.txt"));

            var logBefore = int.Parse(
                (await RunGitCommandRawAsync(cloneDir, ["rev-list", "--count", "HEAD"])).output.Trim());

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            await manager.DeleteFilesAsync(["tracked.txt", "other.txt"], "batch deletion", ct);

            // Exactly ONE commit for both removals.
            var logAfter = int.Parse(
                (await RunGitCommandRawAsync(cloneDir, ["rev-list", "--count", "HEAD"])).output.Trim());
            Assert.Equal(1, logAfter - logBefore);

            // Both paths are gone from the index.
            var (tracked, _) = await RunGitCommandRawAsync(cloneDir, ["ls-files"]);
            Assert.DoesNotContain("tracked.txt", tracked, StringComparison.Ordinal);
            Assert.DoesNotContain("other.txt", tracked, StringComparison.Ordinal);

            // And the single commit was pushed to the remote.
            var (remoteLog, _) = await RunGitCommandRawAsync(bareDir, ["log", "--oneline"]);
            Assert.Contains("batch deletion", remoteLog);
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    [Fact]
    public async Task DeleteFilesAsync_PreCancelledToken_ThrowsOperationCanceled()
    {
        var (bareDir, cloneDir) = await CreateRemoteAndCloneAsync();
        try
        {
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();

            var manager = new ConfigRepoManager(bareDir, cloneDir);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => manager.DeleteFilesAsync(["missing.txt"], "no-op batch deletion", cts.Token));
        }
        finally
        {
            CleanupDirs(bareDir, cloneDir);
        }
    }

    // ── Git helper for tests ──────────────────────────────────────────────────

    private static SemaphoreSlim GetGitLock(ConfigRepoManager manager)
    {
        var field = typeof(ConfigRepoManager).GetField(
            "_gitLock", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var semaphore = field!.GetValue(manager) as SemaphoreSlim;
        Assert.NotNull(semaphore);
        return semaphore!;
    }

    private static async Task RunGitCommandAsync(string workingDir, string[] args)
    {
        var (_, error) = await RunGitCommandRawAsync(workingDir, args);
        _ = error; // errors allowed during test setup
    }

    private static async Task<(string output, string error)> RunGitCommandRawAsync(
        string workingDir, string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Force LF line endings regardless of the host's global/system git config
        // (e.g. Windows installs commonly default core.autocrlf=true) so these tests
        // produce identical file contents on any OS.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.autocrlf=false");
        // Some machines set safe.bareRepository=explicit globally, which blocks
        // running git commands directly against a bare repo directory (as these
        // tests do to inspect the "remote" side). Override so tests work regardless
        // of the host's global git config.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("safe.bareRepository=all");
        // Disable commit signing: a host with commit.gpgsign=true globally configured can
        // make many concurrent `git commit` calls (under high xUnit parallelism) contend for
        // the GPG agent and intermittently fail with "gpg: signing failed: Not enough space"
        // — these test commits don't need to be signed.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("commit.gpgsign=false");
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync();
        return (stdoutTask.Result, stderrTask.Result);
    }
}

// Fake subclass that no-ops CommitFileAsync to avoid real git calls in WriteConfigAsync tests
internal sealed class FakeConfigRepoManager(string url, string path) : ConfigRepoManager(url, path)
{
    public List<(string File, string Message)> Commits { get; } = [];

    public override Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        Commits.Add((filePath, commitMessage));
        return Task.CompletedTask;
    }
}
