using CopilotHive.Configuration;
using CopilotHive.Services;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for <see cref="HiveConfigFile"/> YAML deserialization, focusing on the
/// <c>models.compaction_model</c> configuration added in the compaction-model feature.
/// </summary>
public sealed class HiveConfigFileTests
{
    /// <summary>
    /// The same deserializer configuration used by production code in
    /// <see cref="ConfigRepoManager"/> — underscored naming convention, ignore unmatched.
    /// </summary>
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    // ── Test A: Full models: section deserializes correctly ─────────────────────

    [Fact]
    public void Deserialize_ModelsSection_CompactionModelSet()
    {
        const string yaml = """
            version: "1.0"
            models:
              compaction_model: copilot/gpt-5.4-mini
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config);
        Assert.NotNull(config.Models);
        Assert.Equal("copilot/gpt-5.4-mini", config.Models.CompactionModel);
    }

    // ── Test B: Missing models: section leaves Models null ──────────────────────

    [Fact]
    public void Deserialize_NoModelsSection_ModelsIsNull()
    {
        const string yaml = """
            version: "1.0"
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config);
        Assert.Null(config.Models);
    }

    // ── BranchCleanupDelayHours tests ────────────────────────────────────────

    [Fact]
    public void OrchestratorConfig_BranchCleanupDelayHours_DefaultIs48()
    {
        var config = new OrchestratorConfig();

        Assert.Equal(48, config.BranchCleanupDelayHours);
    }

    [Fact]
    public void OrchestratorConfig_Model_DefaultIsNull()
    {
        var config = new OrchestratorConfig();

        Assert.Null(config.Model);
    }

    [Fact]
    public void CreateEmptyModelFallback_ModelIsNull()
    {
        var config = OrchestratorConfig.CreateEmptyModelFallback();

        Assert.Null(config.Model);
        Assert.NotEqual(Constants.DefaultWorkerModel, config.Model);
    }

    [Fact]
    public void Deserialize_OrchestratorSection_BranchCleanupDelayHoursNotSet_DefaultIs48()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: gpt-4
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config);
        Assert.Equal(48, config.Orchestrator.BranchCleanupDelayHours);
    }

    [Fact]
    public void Deserialize_OrchestratorSection_BranchCleanupDelayHoursSet_UsesValue()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              branch_cleanup_delay_hours: 24
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config);
        Assert.Equal(24, config.Orchestrator.BranchCleanupDelayHours);
    }

    [Fact]
    public void Deserialize_OrchestratorSection_BranchCleanupDelayHoursZero_AllowsZero()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              branch_cleanup_delay_hours: 0
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config);
        Assert.Equal(0, config.Orchestrator.BranchCleanupDelayHours);
    }

    // ── AvailableModels deserialization ─────────────────────────────────────

    [Fact]
    public void Deserialize_AvailableModels_YamlListDeserializesCorrectly()
    {
        const string yaml = """
            version: "1.0"
            models:
              available_models:
                - name: copilot/claude-sonnet-4.6
                  context_window: 200000
                - name: copilot/gpt-5.4-mini
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config);
        Assert.NotNull(config.Models);
        Assert.NotNull(config.Models.AvailableModels);
        Assert.Equal(2, config.Models.AvailableModels.Count);
        Assert.Equal("copilot/claude-sonnet-4.6", config.Models.AvailableModels[0].Name);
        Assert.Equal(200000, config.Models.AvailableModels[0].ContextWindow);
        Assert.Equal("copilot/gpt-5.4-mini", config.Models.AvailableModels[1].Name);
        Assert.Null(config.Models.AvailableModels[1].ContextWindow);
    }

    [Fact]
    public void Deserialize_ModelEntry_ContextWindow_DefaultsToNullWhenOmitted()
    {
        const string yaml = """
            version: "1.0"
            models:
              available_models:
                - name: copilot/gpt-5
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config);
        Assert.NotNull(config.Models);
        Assert.NotNull(config.Models!.AvailableModels);
        Assert.Null(config.Models.AvailableModels[0].ContextWindow);
    }

    // ── IsConfigured marker ──────────────────────────────────────────────────

    [Fact]
    public void IsConfigured_DefaultsToFalse()
    {
        var config = new HiveConfigFile();

        Assert.False(config.IsConfigured);
    }

    [Fact]
    public void IsConfigured_SetterIsNotPublic_OnlyConfigLayerCanSetIt()
    {
        // The provenance invariant: IsConfigured may only be set by the config layer
        // (ConfigRepoManager) — external consumers must not be able to mark a config
        // as repo-parsed. The setter must exist (internal, visible to this test assembly
        // via InternalsVisibleTo) but must NOT be public.
        var property = typeof(HiveConfigFile).GetProperty(nameof(HiveConfigFile.IsConfigured))
            ?? throw new InvalidOperationException("IsConfigured property not found");

        Assert.NotNull(property.SetMethod);
        Assert.False(property.SetMethod!.IsPublic,
            "IsConfigured setter must be non-public (internal) so external consumers cannot set it");
        Assert.True(property.GetMethod!.IsPublic,
            "IsConfigured getter must remain public for read access");
    }

    [Fact]
    public void IsConfigured_NeverAppearsInSerializedYaml()
    {
        var config = new HiveConfigFile { IsConfigured = true };

        var yaml = Serializer.Serialize(config);

        Assert.DoesNotContain("is_configured", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IsConfigured", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void IsConfigured_PreservedAcrossReloadFrom()
    {
        var receiver = new HiveConfigFile { IsConfigured = true };
        var source = new HiveConfigFile { IsConfigured = false, Orchestrator = new OrchestratorConfig { Model = "new-model" } };

        receiver.ReloadFrom(source);

        // The marker reflects that a repo config was loaded onto the singleton: it must NOT
        // be reset by ReloadFrom even though the source instance itself carries false.
        Assert.True(receiver.IsConfigured);
        Assert.Equal("new-model", receiver.Orchestrator.Model);
    }

    // ── ReloadFrom tests ─────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 1: All properties deeply copied from a fully populated source.
    /// </summary>
    [Fact]
    public void ReloadFrom_FullyPopulatedSource_AllPropertiesMatchExactly()
    {
        // Arrange — build a fully-populated source
        var source = new HiveConfigFile
        {
            Version = "2.0",
            Repositories =
            [
                new RepositoryConfig { Name = "repo1", Url = "https://github.com/org/repo1", DefaultBranch = "develop" },
                new RepositoryConfig { Name = "repo2", Url = "https://github.com/org/repo2", DefaultBranch = "main" }
            ],
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new WorkerConfig { Model = "copilot/claude-sonnet-4.6", PremiumModel = "copilot/o3", ContextWindow = 200000 },
                ["tester"] = new WorkerConfig { Model = "copilot/gpt-5.4-mini", PremiumModel = null, ContextWindow = 128000 }
            },
            Orchestrator = new OrchestratorConfig
            {
                Model = "copilot/orchestrator-model",
                MaxIterations = 42,
                MaxRetriesPerTask = 7,
                MaxParallelGoals = 3,
                VerboseLogging = true,
                BrainMaxSteps = 50,
                BranchCleanupDelayHours = 24
            },
            Models = new ModelsConfig
            {
                CompactionModel = "copilot/gpt-5.4-mini",
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ContextWindow = 200000 },
                    new ModelEntry { Name = "copilot/gpt-5.4-mini", ContextWindow = 128000 }
                ]
            },
            Composer = new ComposerConfig
            {
                Model = "copilot/composer-model",
                MaxSteps = 25
            }
        };

        var receiver = new HiveConfigFile();

        // Act
        receiver.ReloadFrom(source);

        // Assert — Version
        Assert.Equal("2.0", receiver.Version);

        // Assert — Repositories
        Assert.Equal(2, receiver.Repositories.Count);
        Assert.Equal("repo1", receiver.Repositories[0].Name);
        Assert.Equal("https://github.com/org/repo1", receiver.Repositories[0].Url);
        Assert.Equal("develop", receiver.Repositories[0].DefaultBranch);
        Assert.Equal("repo2", receiver.Repositories[1].Name);
        Assert.Equal("https://github.com/org/repo2", receiver.Repositories[1].Url);
        Assert.Equal("main", receiver.Repositories[1].DefaultBranch);

        // Assert — Workers
        Assert.Equal(2, receiver.Workers.Count);
        Assert.Equal("copilot/claude-sonnet-4.6", receiver.Workers["coder"].Model);
        Assert.Equal("copilot/o3", receiver.Workers["coder"].PremiumModel);
        Assert.Equal(200000, receiver.Workers["coder"].ContextWindow);
        Assert.Equal("copilot/gpt-5.4-mini", receiver.Workers["tester"].Model);
        Assert.Null(receiver.Workers["tester"].PremiumModel);
        Assert.Equal(128000, receiver.Workers["tester"].ContextWindow);

        // Assert — Orchestrator
        Assert.Equal("copilot/orchestrator-model", receiver.Orchestrator.Model);
        Assert.Equal(42, receiver.Orchestrator.MaxIterations);
        Assert.Equal(7, receiver.Orchestrator.MaxRetriesPerTask);
        Assert.Equal(3, receiver.Orchestrator.MaxParallelGoals);
        Assert.True(receiver.Orchestrator.VerboseLogging);
        Assert.Equal(50, receiver.Orchestrator.BrainMaxSteps);
        Assert.Equal(24, receiver.Orchestrator.BranchCleanupDelayHours);

        // Assert — Models
        Assert.NotNull(receiver.Models);
        Assert.Equal("copilot/gpt-5.4-mini", receiver.Models!.CompactionModel);
        Assert.Equal(2, receiver.Models.AvailableModels!.Count);
        Assert.Equal("copilot/claude-sonnet-4.6", receiver.Models.AvailableModels[0].Name);
        Assert.Equal(200000, receiver.Models.AvailableModels[0].ContextWindow);
        Assert.Equal("copilot/gpt-5.4-mini", receiver.Models.AvailableModels[1].Name);
        Assert.Equal(128000, receiver.Models.AvailableModels[1].ContextWindow);

        // Assert — Composer
        Assert.NotNull(receiver.Composer);
        Assert.Equal("copilot/composer-model", receiver.Composer!.Model);
        Assert.Equal(25, receiver.Composer.MaxSteps);
    }

    /// <summary>
    /// Scenario 2: Null Models and Composer handled correctly — receiver's non-null values become null.
    /// </summary>
    [Fact]
    public void ReloadFrom_NullModelsAndComposer_ReceiverBecomesNull()
    {
        // Arrange — receiver starts with non-null Models and Composer
        var receiver = new HiveConfigFile
        {
            Version = "1.0",
            Models = new ModelsConfig
            {
                CompactionModel = "old-model",
                AvailableModels = [new ModelEntry { Name = "old-entry", ContextWindow = 999 }]
            },
            Composer = new ComposerConfig
            {
                Model = "old-composer",
                MaxSteps = 10
            }
        };

        var source = new HiveConfigFile
        {
            Version = "3.0",
            Models = null,
            Composer = null
        };

        // Act
        receiver.ReloadFrom(source);

        // Assert
        Assert.Equal("3.0", receiver.Version);
        Assert.Null(receiver.Models);
        Assert.Null(receiver.Composer);
    }

    /// <summary>
    /// Scenario 3: Deep copy verification — mutating source after ReloadFrom does not affect receiver.
    /// </summary>
    [Fact]
    public void ReloadFrom_DeepCopy_MutatingSourceDoesNotAffectReceiver()
    {
        // Arrange — build a fully-populated source and reload
        var source = new HiveConfigFile
        {
            Version = "1.0",
            Repositories =
            [
                new RepositoryConfig { Name = "orig-repo", Url = "https://github.com/org/orig", DefaultBranch = "main" }
            ],
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new WorkerConfig { Model = "orig-model", PremiumModel = "orig-premium", ContextWindow = 100000 }
            },
            Orchestrator = new OrchestratorConfig
            {
                Model = "orig-orchestrator",
                MaxIterations = 10,
                MaxRetriesPerTask = 3,
                MaxParallelGoals = 1,
                VerboseLogging = false,
                BrainMaxSteps = 30,
                BranchCleanupDelayHours = 48
            },
            Models = new ModelsConfig
            {
                CompactionModel = "orig-compaction",
                AvailableModels = [new ModelEntry { Name = "orig-model-entry", ContextWindow = 50000 }]
            },
            Composer = new ComposerConfig
            {
                Model = "orig-composer",
                MaxSteps = 15
            }
        };

        var receiver = new HiveConfigFile();
        receiver.ReloadFrom(source);

        // Save receiver values before mutating source
        var receiverRepoCount = receiver.Repositories.Count;
        var receiverCoderModel = receiver.Workers["coder"].Model;
        var receiverOrchestratorModel = receiver.Orchestrator.Model;
        var receiverModelsAvailableCount = receiver.Models!.AvailableModels!.Count;
        var receiverComposerModel = receiver.Composer!.Model;

        // Act — mutate source in every possible way
        source.Repositories.Add(new RepositoryConfig { Name = "new-repo", Url = "https://github.com/org/new", DefaultBranch = "dev" });
        source.Workers["coder"].Model = "mutated-model";
        source.Orchestrator.Model = "mutated-orchestrator";
        source.Models!.AvailableModels!.Add(new ModelEntry { Name = "new-model-entry", ContextWindow = 99999 });
        source.Composer!.Model = "mutated-composer-model";

        // Assert — receiver is NOT affected by any source mutations
        Assert.Equal(receiverRepoCount, receiver.Repositories.Count);
        Assert.Equal(receiverCoderModel, receiver.Workers["coder"].Model);
        Assert.Equal(receiverOrchestratorModel, receiver.Orchestrator.Model);
        Assert.Equal(receiverModelsAvailableCount, receiver.Models!.AvailableModels!.Count);
        Assert.Equal(receiverComposerModel, receiver.Composer!.Model);

        // Also verify the receiver still has the original values
        Assert.Equal("orig-repo", receiver.Repositories[0].Name);
        Assert.Equal("orig-model", receiver.Workers["coder"].Model);
        Assert.Equal("orig-orchestrator", receiver.Orchestrator.Model);
        Assert.Equal("orig-model-entry", receiver.Models.AvailableModels[0].Name);
        Assert.Equal("orig-composer", receiver.Composer!.Model);
    }

    /// <summary>
    /// Scenario 4: Collection replacement — old collections are replaced, not mutated.
    /// Captured references to old collections still hold original data.
    /// </summary>
    [Fact]
    public void ReloadFrom_CollectionReplacement_OldCollectionsPreservedReceiverUpdated()
    {
        // Arrange — receiver with initial data
        var receiver = new HiveConfigFile
        {
            Version = "1.0",
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "old-repo",
                    Url = "https://github.com/org/old",
                    DefaultBranch = "main",
                    Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "main" }
                }
            ],
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["old-role"] = new WorkerConfig { Model = "old-model", PremiumModel = null, ContextWindow = 50000 }
            },
            Models = new ModelsConfig
            {
                CompactionModel = "old-compaction",
                AvailableModels = [new ModelEntry { Name = "old-model-entry", ContextWindow = 30000 }]
            },
            Composer = new ComposerConfig
            {
                Model = "old-composer",
                MaxSteps = 5
            }
        };

        // Capture references before reload
        var oldRepositories = receiver.Repositories;
        var oldWorkers = receiver.Workers;
        var oldAvailableModels = receiver.Models!.AvailableModels!;
        var oldComposerModel = receiver.Composer!.Model;

        // Build a new source with different data
        var source = new HiveConfigFile
        {
            Version = "2.0",
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "new-repo",
                    Url = "https://github.com/org/new",
                    DefaultBranch = "develop",
                    Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "main" }
                }
            ],
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["new-role"] = new WorkerConfig { Model = "new-model", PremiumModel = "new-premium", ContextWindow = 200000 }
            },
            Orchestrator = new OrchestratorConfig
            {
                Model = "new-orchestrator",
                MaxIterations = 99,
                MaxRetriesPerTask = 5,
                MaxParallelGoals = 4,
                VerboseLogging = false,
                BrainMaxSteps = 100,
                BranchCleanupDelayHours = 12
            },
            Models = new ModelsConfig
            {
                CompactionModel = "new-compaction",
                AvailableModels = [new ModelEntry { Name = "new-model-entry", ContextWindow = 200000 }]
            },
            Composer = new ComposerConfig
            {
                Model = "new-composer",
                MaxSteps = 20
            }
        };

        // Act
        receiver.ReloadFrom(source);

        // Assert — old captured collections still hold the original data
        Assert.Single(oldRepositories);
        Assert.Equal("old-repo", oldRepositories[0].Name);
        Assert.Equal("https://github.com/org/old", oldRepositories[0].Url);
        Assert.Equal("main", oldRepositories[0].DefaultBranch);
        Assert.Equal("main", oldRepositories[0].Release!.MergeTo);
        Assert.Equal("main", oldRepositories[0].Release!.TagBranch);

        Assert.Single(oldWorkers);
        Assert.True(oldWorkers.ContainsKey("old-role"));
        Assert.Equal("old-model", oldWorkers["old-role"].Model);

        Assert.Single(oldAvailableModels);
        Assert.Equal("old-model-entry", oldAvailableModels[0].Name);

        Assert.Equal("old-composer", oldComposerModel);

        // Assert — receiver now references entirely new collections
        Assert.NotSame(oldRepositories, receiver.Repositories);
        Assert.NotSame(oldWorkers, receiver.Workers);
        Assert.NotSame(oldAvailableModels, receiver.Models!.AvailableModels);
        Assert.NotSame(oldComposerModel, receiver.Composer!.Model);

        // Assert — release config is also deep-copied (not the same reference)
        Assert.NotSame(source.Repositories[0].Release, receiver.Repositories[0].Release);

        // Assert — receiver's new data matches the source
        Assert.Equal("2.0", receiver.Version);
        Assert.Single(receiver.Repositories);
        Assert.Equal("new-repo", receiver.Repositories[0].Name);
        Assert.Equal("main", receiver.Repositories[0].Release!.MergeTo);
        Assert.Equal("main", receiver.Repositories[0].Release!.TagBranch);
        Assert.Single(receiver.Workers);
        Assert.Equal("new-model", receiver.Workers["new-role"].Model);
        Assert.Equal("new-orchestrator", receiver.Orchestrator.Model);
        Assert.Equal("new-compaction", receiver.Models.CompactionModel);
        Assert.Single(receiver.Models.AvailableModels!);
        Assert.Equal("new-model-entry", receiver.Models.AvailableModels[0].Name);
        Assert.Equal("new-composer", receiver.Composer!.Model);
    }

    // ── ReloadFrom Release deep-copy tests ───────────────────────────────────

    [Fact]
    public void ReloadFrom_WithRelease_DeepCopiesReleaseInstance()
    {
        var source = new HiveConfigFile
        {
            Version = "1.0",
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-repo",
                    Url = "https://github.com/org/repo",
                    DefaultBranch = "main",
                    Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "develop" }
                }
            ]
        };
        var receiver = new HiveConfigFile { Version = "1.0", Repositories = [], Workers = new Dictionary<string, WorkerConfig>() };

        receiver.ReloadFrom(source);

        Assert.NotNull(receiver.Repositories[0].Release);
        Assert.NotSame(source.Repositories[0].Release, receiver.Repositories[0].Release);
        Assert.Equal("main", receiver.Repositories[0].Release!.MergeTo);
        Assert.Equal("develop", receiver.Repositories[0].Release!.TagBranch);
    }

    [Fact]
    public void ReloadFrom_WithNullRelease_ReceiverReleaseIsNull()
    {
        var source = new HiveConfigFile
        {
            Version = "1.0",
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-repo",
                    Url = "https://github.com/org/repo",
                    DefaultBranch = "main",
                    Release = null
                }
            ]
        };
        var receiver = new HiveConfigFile { Version = "1.0", Repositories = [], Workers = new Dictionary<string, WorkerConfig>() };

        receiver.ReloadFrom(source);

        Assert.Null(receiver.Repositories[0].Release);
    }

    [Fact]
    public void ReloadFrom_DeepCopy_MutatingSourceReleaseDoesNotAffectReceiver()
    {
        var source = new HiveConfigFile
        {
            Version = "1.0",
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-repo",
                    Url = "https://github.com/org/repo",
                    DefaultBranch = "main",
                    Release = new ReleaseRepoConfig { MergeTo = "main", TagBranch = "main" }
                }
            ]
        };
        var receiver = new HiveConfigFile { Version = "1.0", Repositories = [], Workers = new Dictionary<string, WorkerConfig>() };

        receiver.ReloadFrom(source);

        // Mutate the source after ReloadFrom
        source.Repositories[0].Release!.MergeTo = "mutated";

        // Receiver should be unaffected
        Assert.Equal("main", receiver.Repositories[0].Release!.MergeTo);
    }

    // ── GetComposerAvailableModels tests (parameterless normalized catalog) ─────

    /// <summary>
    /// When Models.AvailableModels is populated, GetComposerAvailableModels returns
    /// the normalized global names.
    /// </summary>
    [Fact]
    public void GetComposerAvailableModels_GlobalAvailableModels_ReturnsGlobalNames()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6" },
                    new ModelEntry { Name = "copilot/gpt-5.4-mini" },
                    new ModelEntry { Name = "copilot/o3" }
                ]
            }
        };

        var result = config.GetComposerAvailableModels();

        Assert.Equal(3, result.Count);
        Assert.Equal("copilot/claude-sonnet-4.6", result[0]);
        Assert.Equal("copilot/gpt-5.4-mini", result[1]);
        Assert.Equal("copilot/o3", result[2]);
    }

    /// <summary>
    /// When Models is null, GetComposerAvailableModels returns an EMPTY list — the
    /// composer-local list is NOT a fall-through source for the selectable catalog.
    /// </summary>
    [Fact]
    public void GetComposerAvailableModels_GlobalModelsNull_ReturnsEmptyList()
    {
        var config = new HiveConfigFile
        {
            Models = null,
            Composer = new ComposerConfig
            {
                Model = "composer-model"
            }
        };

        var result = config.GetComposerAvailableModels();

        // NO composer-local fall-through: the catalog is the global list only.
        Assert.Empty(result);
    }

    /// <summary>
    /// When Models.AvailableModels is an empty list, GetComposerAvailableModels
    /// returns an empty list — no fabricated fallback.
    /// </summary>
    [Fact]
    public void GetComposerAvailableModels_GlobalAvailableModelsEmpty_ReturnsEmptyList()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = []
            },
            Composer = new ComposerConfig
            {
                Model = "fallback-model"
            }
        };

        var result = config.GetComposerAvailableModels();

        // Empty global list ⇒ empty catalog — no composer-local/fabricated fallback.
        Assert.Empty(result);
    }

    /// <summary>
    /// When both Models and Composer are null, GetComposerAvailableModels
    /// returns an empty list (no fabricated fallback model is ever invented).
    /// </summary>
    [Fact]
    public void GetComposerAvailableModels_BothNull_ReturnsEmptyList()
    {
        var config = new HiveConfigFile
        {
            Models = null,
            Composer = null
        };

        var result = config.GetComposerAvailableModels();

        Assert.Empty(result);
    }

    /// <summary>
    /// When Models has AvailableModels but Composer is null, the global list
    /// is still returned (the catalog is global-only).
    /// </summary>
    [Fact]
    public void GetComposerAvailableModels_GlobalListPresent_ComposerNull_ReturnsGlobalList()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "global-only-model" }
                ]
            },
            Composer = null
        };

        var result = config.GetComposerAvailableModels();

        Assert.Single(result);
        Assert.Equal("global-only-model", result[0]);
    }

    /// <summary>
    /// The catalog is normalized: names are trimmed, whitespace-only/empty entries are dropped,
    /// and ordinal-ignore-case duplicates collapse to the FIRST occurrence.
    /// </summary>
    [Fact]
    public void GetComposerAvailableModels_NormalizesCatalog_TrimsDropsDuplicatesFirstWins()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "  GPT-4  " },
                    new ModelEntry { Name = "gpt-4" },
                    new ModelEntry { Name = "   " },
                    new ModelEntry { Name = "" },
                    new ModelEntry { Name = "Claude-Opus" },
                    new ModelEntry { Name = "claude-opus" }
                ]
            }
        };

        var result = config.GetComposerAvailableModels();

        Assert.Equal(2, result.Count);
        Assert.Equal("GPT-4", result[0]);
        Assert.Equal("Claude-Opus", result[1]);
    }

    // ── ResolveAvailableModel tests (the shared matching primitive) ────────────

    [Fact]
    public void ResolveAvailableModel_TrimsBothSides_ReturnsTrimmedCanonical()
    {
        var config = new HiveConfigFile();

        var result = config.ResolveAvailableModel(["  GPT-4  ", "claude-opus"], "  gpt-4  ");

        Assert.Equal("GPT-4", result);
    }

    [Fact]
    public void ResolveAvailableModel_WhitespaceBearingEntry_ReturnsTrimmedCanonical()
    {
        var config = new HiveConfigFile();

        var result = config.ResolveAvailableModel(["  GPT-4  "], "GPT-4");

        Assert.Equal("GPT-4", result);
    }

    [Fact]
    public void ResolveAvailableModel_DropsWhitespaceOnlyAndEmptyEntries()
    {
        var config = new HiveConfigFile();

        var result = config.ResolveAvailableModel(["   ", "", "GPT-4"], "gpt-4");

        Assert.Equal("GPT-4", result);
    }

    [Fact]
    public void ResolveAvailableModel_OrdinalIgnoreCase_MatchIsCaseInsensitive()
    {
        var config = new HiveConfigFile();

        var result = config.ResolveAvailableModel(["Copilot/Claude-Sonnet-4.6"], "copilot/claude-sonnet-4.6");

        Assert.Equal("Copilot/Claude-Sonnet-4.6", result);
    }

    [Fact]
    public void ResolveAvailableModel_DuplicatesCollapseToFirst()
    {
        var config = new HiveConfigFile();

        // The FIRST normalized duplicate wins — the canonical name is the first entry's trimmed form.
        var result = config.ResolveAvailableModel(["  First-Model  ", "first-model"], "FIRST-MODEL");

        Assert.Equal("First-Model", result);
    }

    [Fact]
    public void ResolveAvailableModel_NoMatch_ReturnsNull()
    {
        var config = new HiveConfigFile();

        Assert.Null(config.ResolveAvailableModel(["model-a", "model-b"], "model-c"));
    }

    [Fact]
    public void ResolveAvailableModel_EmptyCatalog_ReturnsNull()
    {
        var config = new HiveConfigFile();

        Assert.Null(config.ResolveAvailableModel([], "any-model"));
        Assert.Null(config.ResolveAvailableModel(null, "any-model"));
    }

    [Fact]
    public void ResolveAvailableModel_NullCandidate_ReturnsNull()
    {
        var config = new HiveConfigFile();

        Assert.Null(config.ResolveAvailableModel(["GPT-4"], null));
        Assert.Null(config.ResolveAvailableModel(["GPT-4"], "   "));
        Assert.Null(config.ResolveAvailableModel(["GPT-4"], ""));
    }

    [Fact]
    public void ResolveAvailableModel_GlobalOverload_DelegatesToGlobalCatalog()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "  copilot/gpt-5.4-mini  " },
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6" }
                ]
            }
        };

        var result = config.ResolveAvailableModel("COPILOT/GPT-5.4-MINI");

        Assert.Equal("copilot/gpt-5.4-mini", result);
        Assert.Null(config.ResolveAvailableModel("not-in-catalog"));
    }

    /// <summary>
    /// Null entries in the catalog are silently skipped (not fatal): a valid match
    /// AFTER a null entry is still resolved.
    /// </summary>
    [Fact]
    public void ResolveAvailableModel_NullEntriesInCatalog_SkippedAndMatchStillFound()
    {
        var config = new HiveConfigFile();

        // null string entries interspersed — the primitive must skip them, not throw.
        var result = config.ResolveAvailableModel([null!, "  GPT-4  ", null!], "gpt-4");

        Assert.Equal("GPT-4", result);
    }

    /// <summary>
    /// A whitespace-bearing candidate resolves a cleanly-stored catalog entry, and
    /// returns the entry's canonical (trimmed) form.
    /// </summary>
    [Fact]
    public void ResolveAvailableModel_WhitespaceCandidate_ResolvesCleanEntry()
    {
        var config = new HiveConfigFile();

        var result = config.ResolveAvailableModel(["copilot/claude-sonnet-4.6"], "  COPILOT/CLAUDE-SONNET-4.6  ");

        Assert.Equal("copilot/claude-sonnet-4.6", result);
    }

    /// <summary>
    /// When all catalog entries are whitespace-only/empty/null and the candidate is
    /// valid, the result is null (no match survives normalization).
    /// </summary>
    [Fact]
    public void ResolveAvailableModel_AllEntriesWhitespaceOnly_ReturnsNull()
    {
        var config = new HiveConfigFile();

        Assert.Null(config.ResolveAvailableModel(["  ", "", null!], "any-model"));
    }

    // ── ResolveComposerDefaultModel tests ────────────────────────────────────

    [Fact]
    public void ResolveComposerDefaultModel_PresentAndInGlobalCatalog_ReturnsTrimmedCanonical()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "  copilot/composer-model  " }
                ]
            },
            Composer = new ComposerConfig { Model = "  COPILOT/COMPOSER-MODEL  " }
        };

        var result = config.ResolveComposerDefaultModel();

        Assert.Equal("copilot/composer-model", result);
    }

    [Fact]
    public void ResolveComposerDefaultModel_SetButAbsentFromCatalog_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "other-model" }]
            },
            Composer = new ComposerConfig { Model = "composer-model" }
        };

        Assert.Null(config.ResolveComposerDefaultModel());
    }

    [Fact]
    public void ResolveComposerDefaultModel_Unset_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "model-a" }]
            },
            Composer = new ComposerConfig { Model = null }
        };

        Assert.Null(config.ResolveComposerDefaultModel());
    }

    [Fact]
    public void ResolveComposerDefaultModel_WhitespaceOnly_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "model-a" }]
            },
            Composer = new ComposerConfig { Model = "   " }
        };

        Assert.Null(config.ResolveComposerDefaultModel());
    }

    [Fact]
    public void ResolveComposerDefaultModel_NoGlobalCatalog_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Composer = new ComposerConfig { Model = "composer-model" }
        };

        Assert.Null(config.ResolveComposerDefaultModel());
    }

    [Fact]
    public void ResolveComposerDefaultModel_NoComposerSection_ReturnsNull()
    {
        var config = new HiveConfigFile();

        Assert.Null(config.ResolveComposerDefaultModel());
    }

    // ── TryGetContextWindowForModel tests (Brain & Composer resolution) ─────────

    [Fact]
    public void TryGetContextWindowForModel_ModelFound_ReturnsContextWindow()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "brain-model", ContextWindow = 200_000 },
                    new ModelEntry { Name = "composer-model", ContextWindow = 128_000 }
                ]
            }
        };

        // Brain resolution: model-specific value returned
        Assert.Equal(200_000, config.TryGetContextWindowForModel("brain-model"));
        // Composer resolution: model-specific value returned
        Assert.Equal(128_000, config.TryGetContextWindowForModel("composer-model"));
    }

    [Fact]
    public void TryGetContextWindowForModel_NullInput_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "known-model", ContextWindow = 200_000 }
                ]
            }
        };

        Assert.Null(config.TryGetContextWindowForModel(null));
    }

    [Fact]
    public void TryGetContextWindowForModel_ModelNotFound_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "known-model", ContextWindow = 200_000 }
                ]
            }
        };

        // Brain resolution: model not found → null (caller falls back to DefaultBrainContextWindow)
        Assert.Null(config.TryGetContextWindowForModel("unknown-brain-model"));
        // Composer resolution: model not found → null
        Assert.Null(config.TryGetContextWindowForModel("unknown-composer-model"));
    }

    [Fact]
    public void TryGetContextWindowForModel_NoModelsSection_ReturnsNull()
    {
        var config = new HiveConfigFile();

        // No Models section at all → null for any model
        Assert.Null(config.TryGetContextWindowForModel("any-model"));
    }

    [Fact]
    public void TryGetContextWindowForModel_EmptyAvailableModels_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig { AvailableModels = [] }
        };

        Assert.Null(config.TryGetContextWindowForModel("any-model"));
    }

    [Fact]
    public void TryGetContextWindowForModel_CaseInsensitive_ReturnsContextWindow()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "Copilot/Claude-Sonnet-4.6", ContextWindow = 200_000 }
                ]
            }
        };

        Assert.Equal(200_000, config.TryGetContextWindowForModel("copilot/claude-sonnet-4.6"));
        Assert.Equal(200_000, config.TryGetContextWindowForModel("COPILOT/CLAUDE-SONNET-4.6"));
    }

    [Fact]
    public void TryGetContextWindowForModel_StoredNameWithSurroundingWhitespace_ResolvesTrimmedCanonical()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "  copilot/trimmed-model  ", ContextWindow = 175_000 }
                ]
            }
        };

        // A trimmed canonical name resolves the entry whose stored name carries whitespace.
        Assert.Equal(175_000, config.TryGetContextWindowForModel("copilot/trimmed-model"));
    }

    [Fact]
    public void TryGetContextWindowForModel_NormalizedDuplicates_FirstWins()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "  dup-model  ", ContextWindow = 111_000 },
                    new ModelEntry { Name = "DUP-MODEL", ContextWindow = 222_000 }
                ]
            }
        };

        // FIRST normalized case-insensitive duplicate wins.
        Assert.Equal(111_000, config.TryGetContextWindowForModel("dup-model"));
    }

    /// <summary>
    /// A whitespace-bearing candidate resolves a cleanly-stored entry (reverse direction
    /// of <see cref="TryGetContextWindowForModel_StoredNameWithSurroundingWhitespace_ResolvesTrimmedCanonical"/>).
    /// </summary>
    [Fact]
    public void TryGetContextWindowForModel_WhitespaceCandidate_ResolvesCleanEntry()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/clean-model", ContextWindow = 99_000 }
                ]
            }
        };

        // Candidate carries surrounding whitespace; the stored entry is clean.
        Assert.Equal(99_000, config.TryGetContextWindowForModel("  COPILOT/CLEAN-MODEL  "));
    }

    // ── YAML backward compatibility: removed fields still deserialize ───────────

    [Fact]
    public void Deserialize_OrchestratorBrainContextWindow_IgnoredWithoutError()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: copilot/test-model
              brain_context_window: 256000
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config);
        Assert.Equal("copilot/test-model", config.Orchestrator.Model);
    }

    [Fact]
    public void Deserialize_OrchestratorWorkerContextWindow_IgnoredWithoutError()
    {
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: copilot/test-model
              worker_context_window: 128000
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config);
        Assert.Equal("copilot/test-model", config.Orchestrator.Model);
    }

    [Fact]
    public void Deserialize_ComposerContextWindow_IgnoredWithoutError()
    {
        const string yaml = """
            version: "1.0"
            composer:
              model: copilot/composer-model
              context_window: 100000
              max_steps: 50
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config);
        Assert.NotNull(config.Composer);
        Assert.Equal("copilot/composer-model", config.Composer.Model);
        Assert.Equal(50, config.Composer.MaxSteps);
    }

    [Fact]
    public void Deserialize_AllRemovedFieldsPresent_IgnoredWithoutError()
    {
        // All three removed fields present in the same YAML — should all be ignored
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: copilot/test-model
              brain_context_window: 300000
              worker_context_window: 180000
              brain_max_steps: 75
            composer:
              model: copilot/composer-model
              context_window: 160000
              max_steps: 25
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config);
        Assert.Equal("copilot/test-model", config.Orchestrator.Model);
        Assert.Equal(75, config.Orchestrator.BrainMaxSteps);
        Assert.NotNull(config.Composer);
        Assert.Equal("copilot/composer-model", config.Composer.Model);
        Assert.Equal(25, config.Composer.MaxSteps);
    }

    [Fact]
    public void Deserialize_OrchestratorAlwaysImprove_IgnoredWithoutError()
    {
        // Stale `always_improve` key must be silently dropped by IgnoreUnmatchedProperties().
        const string yaml = """
            version: "1.0"
            orchestrator:
              model: copilot/test-model
              always_improve: true
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config);
        Assert.Equal("copilot/test-model", config.Orchestrator.Model);
    }

    // ── TryGetContextWindowForModel: plain model-name lookup ───────────────────

    /// <summary>
    /// Model names are matched verbatim: a legacy <c>:high</c> suffix is no longer stripped,
    /// so a suffixed name does not resolve to the bare entry.
    /// </summary>
    [Fact]
    public void TryGetContextWindowForModel_LegacySuffix_NotStripped_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ContextWindow = 200_000 }
                ]
            }
        };

        Assert.Null(config.TryGetContextWindowForModel("copilot/claude-sonnet-4.6:high"));
        Assert.Equal(200_000, config.TryGetContextWindowForModel("copilot/claude-sonnet-4.6"));
    }

    /// <summary>
    /// Plain model names resolve exactly, case-insensitively.
    /// </summary>
    [Fact]
    public void TryGetContextWindowForModel_PlainName_ReturnsContextWindow()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ContextWindow = 200_000 }
                ]
            }
        };

        Assert.Equal(200_000, config.TryGetContextWindowForModel("copilot/claude-sonnet-4.6"));
        Assert.Equal(200_000, config.TryGetContextWindowForModel("COPILOT/CLAUDE-SONNET-4.6"));
    }

    /// <summary>
    /// A model tag such as <c>:120b</c> is part of the name and must be matched verbatim.
    /// </summary>
    [Fact]
    public void TryGetContextWindowForModel_ModelTag_MatchedVerbatim()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "ollama-cloud/gpt-oss:120b", ContextWindow = 128_000 }
                ]
            }
        };

        Assert.Equal(128_000, config.TryGetContextWindowForModel("ollama-cloud/gpt-oss:120b"));
        Assert.Null(config.TryGetContextWindowForModel("ollama-cloud/gpt-oss"));
    }

    /// <summary>
    /// An unknown model returns null.
    /// </summary>
    [Fact]
    public void TryGetContextWindowForModel_UnknownModel_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ContextWindow = 200_000 }
                ]
            }
        };

        Assert.Null(config.TryGetContextWindowForModel("copilot/unknown-model"));
    }

    // ── Description / SubAgentModels ─────────────────────────────────────────

    /// <summary>
    /// Serializes <paramref name="config"/> through the production write path
    /// (<see cref="ConfigRepoManager.WriteConfigAsync"/>) and returns the raw YAML text.
    /// This proves the fields survive the real serializer, not just a hand-written YAML fixture.
    /// </summary>
    private static async Task<string> WriteThroughProductionSerializerAsync(HiveConfigFile config)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hive-config-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var repo = new ConfigRepoManager("https://example.com/config.git", dir);
            await repo.WriteConfigAsync(config, TestContext.Current.CancellationToken);
            return await File.ReadAllTextAsync(
                Path.Combine(dir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RoundTrip_ModelEntryDescription_SurvivesSerializeAndDeserialize()
    {
        var original = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "model-a", ContextWindow = 200000, Description = "Fast and cheap" }
                ]
            }
        };

        var yaml = await WriteThroughProductionSerializerAsync(original);

        // The serializer must actually emit the field.
        Assert.Contains("description: Fast and cheap", yaml, StringComparison.Ordinal);

        var reloaded = Deserializer.Deserialize<HiveConfigFile>(yaml);
        var entry = Assert.Single(reloaded.Models!.AvailableModels!);
        Assert.Equal("model-a", entry.Name);
        Assert.Equal(200000, entry.ContextWindow);
        Assert.Equal("Fast and cheap", entry.Description);
    }

    [Fact]
    public async Task RoundTrip_SubAgentModels_SurvivesSerializeAndDeserialize()
    {
        var original = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "available-a" }],
                SubAgentModels =
                [
                    new ModelEntry
                    {
                        Name = "curated-a",
                        ContextWindow = 64000,
                        ReasoningEffort = "high",
                        Description = "Research helper"
                    }
                ]
            }
        };

        var yaml = await WriteThroughProductionSerializerAsync(original);

        Assert.Contains("sub_agent_models:", yaml, StringComparison.Ordinal);
        Assert.Contains("curated-a", yaml, StringComparison.Ordinal);
        Assert.Contains("Research helper", yaml, StringComparison.Ordinal);

        var reloaded = Deserializer.Deserialize<HiveConfigFile>(yaml);
        var entry = Assert.Single(reloaded.Models!.SubAgentModels!);
        Assert.Equal("curated-a", entry.Name);
        Assert.Equal(64000, entry.ContextWindow);
        Assert.Equal("high", entry.ReasoningEffort);
        Assert.Equal("Research helper", entry.Description);

        // available_models must still round-trip alongside the new curated list.
        Assert.Equal("available-a", Assert.Single(reloaded.Models.AvailableModels!).Name);
    }

    [Fact]
    public void ReloadFrom_DeepCopiesEntries_InBothModelCollections()
    {
        var sourceAvailable = new ModelEntry
        {
            Name = "a",
            ContextWindow = 100,
            ReasoningEffort = "low",
            Description = "desc-a"
        };
        var sourceCurated = new ModelEntry
        {
            Name = "b",
            ContextWindow = 200,
            ReasoningEffort = "medium",
            Description = "desc-b"
        };

        var target = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var source = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [sourceAvailable],
                SubAgentModels = [sourceCurated]
            }
        };

        target.ReloadFrom(source);

        var copiedAvailable = Assert.Single(target.Models!.AvailableModels!);
        var copiedCurated = Assert.Single(target.Models.SubAgentModels!);
        Assert.NotSame(sourceAvailable, copiedAvailable);
        Assert.NotSame(sourceCurated, copiedCurated);

        // Mutate EVERY field on both source entries in place.
        sourceAvailable.Name = "mutated-a";
        sourceAvailable.ContextWindow = 999;
        sourceAvailable.ReasoningEffort = "extra_high";
        sourceAvailable.Description = "changed-a";

        sourceCurated.Name = "mutated-b";
        sourceCurated.ContextWindow = 888;
        sourceCurated.ReasoningEffort = "extra_high";
        sourceCurated.Description = "changed-b";

        // The reloaded target must retain the original values in BOTH collections.
        Assert.Equal("a", copiedAvailable.Name);
        Assert.Equal(100, copiedAvailable.ContextWindow);
        Assert.Equal("low", copiedAvailable.ReasoningEffort);
        Assert.Equal("desc-a", copiedAvailable.Description);

        Assert.Equal("b", copiedCurated.Name);
        Assert.Equal(200, copiedCurated.ContextWindow);
        Assert.Equal("medium", copiedCurated.ReasoningEffort);
        Assert.Equal("desc-b", copiedCurated.Description);
    }

    [Fact]
    public void GetSubAgentModels_ReturnsCuratedWhenNonEmpty()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "a" }],
                SubAgentModels = [new ModelEntry { Name = "b" }]
            }
        };

        Assert.Equal("b", Assert.Single(config.GetSubAgentModels()).Name);
    }

    [Fact]
    public void GetSubAgentModels_FallsBackToAvailableModels()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = "a" }] }
        };

        Assert.Equal("a", Assert.Single(config.GetSubAgentModels()).Name);
    }

    [Fact]
    public void GetSubAgentModels_ReturnsEmptyWhenNothingConfigured()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };

        Assert.Empty(config.GetSubAgentModels());
    }

    [Fact]
    public void GetSubAgentModels_MergesNullContextWindowFromAvailable()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "m", ContextWindow = 976000 }],
                SubAgentModels = [new ModelEntry { Name = "m" }]
            }
        };

        var result = Assert.Single(config.GetSubAgentModels());
        Assert.Equal(976000, result.ContextWindow);
    }

    [Fact]
    public void GetSubAgentModels_DoesNotInheritReasoningEffortFromAvailable()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "m", ReasoningEffort = "high" }],
                SubAgentModels = [new ModelEntry { Name = "m" }]
            }
        };

        var result = Assert.Single(config.GetSubAgentModels());
        Assert.Null(result.ReasoningEffort);
    }

    [Fact]
    public void GetSubAgentModels_MergesNullDescriptionFromAvailable()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "m", Description = "merged desc" }],
                SubAgentModels = [new ModelEntry { Name = "m" }]
            }
        };

        var result = Assert.Single(config.GetSubAgentModels());
        Assert.Equal("merged desc", result.Description);
    }

    [Fact]
    public void GetSubAgentModels_KeepsCuratedContextWindowWhenSet()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "m", ContextWindow = 976000 }],
                SubAgentModels = [new ModelEntry { Name = "m", ContextWindow = 128000 }]
            }
        };

        var result = Assert.Single(config.GetSubAgentModels());
        Assert.Equal(128000, result.ContextWindow);
    }

    [Fact]
    public void GetSubAgentModels_MatchesAvailableEntryCaseInsensitively()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "my-model", ContextWindow = 4000 }],
                SubAgentModels = [new ModelEntry { Name = "My-Model" }]
            }
        };

        var result = Assert.Single(config.GetSubAgentModels());
        Assert.Equal(4000, result.ContextWindow);
    }

    [Fact]
    public void GetSubAgentModels_UnmatchedCuratedNameKeepsNulls()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "other", ContextWindow = 4000 }],
                SubAgentModels = [new ModelEntry { Name = "unmatched" }]
            }
        };

        var result = Assert.Single(config.GetSubAgentModels());
        Assert.Equal("unmatched", result.Name);
        Assert.Null(result.ContextWindow);
        Assert.Null(result.ReasoningEffort);
        Assert.Null(result.Description);
    }

    [Fact]
    public void GetSubAgentModels_DoesNotMutateSourceLists()
    {
        var available = new ModelEntry { Name = "m", ContextWindow = 976000 };
        var curated = new ModelEntry { Name = "m" };
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [available],
                SubAgentModels = [curated]
            }
        };

        config.GetSubAgentModels();

        Assert.Null(curated.ContextWindow);
        Assert.Equal(976000, available.ContextWindow);
    }

    [Fact]
    public void GetSubAgentModels_AfterReloadFrom_MergesFields()
    {
        var target = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var source = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "m", ContextWindow = 976000, ReasoningEffort = "high", Description = "merged desc" }],
                SubAgentModels = [new ModelEntry { Name = "m", ReasoningEffort = "low" }]
            }
        };

        target.ReloadFrom(source);

        var result = Assert.Single(target.GetSubAgentModels());
        Assert.Equal(976000, result.ContextWindow);
        Assert.Equal("low", result.ReasoningEffort);
        Assert.Equal("merged desc", result.Description);
    }

    // ── SupportsVision YAML round-trip (tri-state: true, false, null) ──────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task RoundTrip_ModelEntrySupportsVision_PreservesTriState(bool? value)
    {
        var original = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "vision-model", ContextWindow = 200000, SupportsVision = value }
                ]
            }
        };

        var yaml = await WriteThroughProductionSerializerAsync(original);

        if (value is null)
        {
            // null is omitted by the serializer (OmitNull) — must NOT appear in YAML
            Assert.DoesNotContain("supports_vision", yaml, StringComparison.Ordinal);
        }
        else
        {
            // true/false must be emitted as YAML booleans
            var expected = value is true ? "true" : "false";
            Assert.Contains($"supports_vision: {expected}", yaml, StringComparison.Ordinal);
        }

        var reloaded = Deserializer.Deserialize<HiveConfigFile>(yaml);
        var entry = Assert.Single(reloaded.Models!.AvailableModels!);
        Assert.Equal("vision-model", entry.Name);
        Assert.Equal(value, entry.SupportsVision);
    }

    [Fact]
    public async Task RoundTrip_SubAgentModelSupportsVision_PreservesTriState()
    {
        var original = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "base" }],
                SubAgentModels =
                [
                    new ModelEntry { Name = "curated-true", SupportsVision = true },
                    new ModelEntry { Name = "curated-false", SupportsVision = false },
                    new ModelEntry { Name = "curated-unset", SupportsVision = null },
                ]
            }
        };

        var yaml = await WriteThroughProductionSerializerAsync(original);

        Assert.Contains("supports_vision: true", yaml, StringComparison.Ordinal);
        Assert.Contains("supports_vision: false", yaml, StringComparison.Ordinal);

        var reloaded = Deserializer.Deserialize<HiveConfigFile>(yaml);
        var sub = reloaded.Models!.SubAgentModels!;
        Assert.Equal(3, sub.Count);
        Assert.True(sub[0].SupportsVision);
        Assert.False(sub[1].SupportsVision);
        Assert.Null(sub[2].SupportsVision);
    }

    // ── ReloadFrom deep-copies SupportsVision ─────────────────────────────────

    [Fact]
    public void ReloadFrom_DeepCopiesSupportsVision_OnModelEntries()
    {
        var sourceAvailable = new ModelEntry
        {
            Name = "a",
            ContextWindow = 100,
            SupportsVision = true
        };
        var sourceCurated = new ModelEntry
        {
            Name = "b",
            ContextWindow = 200,
            SupportsVision = false
        };

        var target = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var source = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [sourceAvailable],
                SubAgentModels = [sourceCurated]
            }
        };

        target.ReloadFrom(source);

        var copiedAvailable = Assert.Single(target.Models!.AvailableModels!);
        var copiedCurated = Assert.Single(target.Models.SubAgentModels!);
        Assert.NotSame(sourceAvailable, copiedAvailable);
        Assert.NotSame(sourceCurated, copiedCurated);
        Assert.True(copiedAvailable.SupportsVision);
        Assert.False(copiedCurated.SupportsVision);

        // Mutate source — receiver must be unaffected
        sourceAvailable.SupportsVision = false;
        sourceCurated.SupportsVision = true;

        Assert.True(copiedAvailable.SupportsVision);
        Assert.False(copiedCurated.SupportsVision);
    }

    // ── GetSubAgentModels merge: SupportsVision preserve-null (regression) ─────

    [Fact]
    public void GetSubAgentModels_CuratedSupportsVisionTrue_MergedIsTrue()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "m", SupportsVision = false }],
                SubAgentModels = [new ModelEntry { Name = "m", SupportsVision = true }]
            }
        };

        var result = Assert.Single(config.GetSubAgentModels());
        Assert.True(result.SupportsVision);
    }

    [Fact]
    public void GetSubAgentModels_CuratedUnsetInheritsTrueFromAvailable()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "m", SupportsVision = true }],
                SubAgentModels = [new ModelEntry { Name = "m", SupportsVision = null }]
            }
        };

        var result = Assert.Single(config.GetSubAgentModels());
        Assert.True(result.SupportsVision);
    }

    /// <summary>
    /// THE critical regression test: curated unset + available also unset → merged stays null
    /// (NOT false). The merge must preserve the distinction between merged-null and downstream-false.
    /// </summary>
    [Fact]
    public void GetSubAgentModels_CuratedUnsetAndAvailableUnset_MergedStaysNull()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "m", SupportsVision = null }],
                SubAgentModels = [new ModelEntry { Name = "m", SupportsVision = null }]
            }
        };

        var result = Assert.Single(config.GetSubAgentModels());
        Assert.Null(result.SupportsVision);
    }

    [Fact]
    public void GetSubAgentModels_CuratedExplicitFalse_OverridesAvailableTrue()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "m", SupportsVision = true }],
                SubAgentModels = [new ModelEntry { Name = "m", SupportsVision = false }]
            }
        };

        var result = Assert.Single(config.GetSubAgentModels());
        Assert.False(result.SupportsVision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public void GetSubAgentModels_AvailableOnlyModel_KeepsItsOwnSupportsVision(bool? vision)
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "avail-only", SupportsVision = vision }]
            }
        };

        var result = Assert.Single(config.GetSubAgentModels());
        Assert.Equal(vision, result.SupportsVision);
    }

    [Fact]
    public void GetSubAgentModels_DoesNotMutateSourceSupportsVision()
    {
        var available = new ModelEntry { Name = "m", SupportsVision = true };
        var curated = new ModelEntry { Name = "m", SupportsVision = null };
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [available],
                SubAgentModels = [curated]
            }
        };

        config.GetSubAgentModels();

        Assert.Null(curated.SupportsVision);
        Assert.True(available.SupportsVision);
    }

    // ── Reasoning effort: YAML round-trip of the new config fields ─────────────

    [Fact]
    public void Deserialize_ReasoningEffortFields_PopulateWorkersOrchestratorAndComposer()
    {
        const string yaml = """
            version: "1.0"
            workers:
              coder:
                model: claude-opus-4.6
                reasoning_effort: high
                premium_model: gpt-5.4
                premium_reasoning_effort: extra_high
              tester:
                model: claude-sonnet-4.6
                reasoning_effort: medium
            orchestrator:
              model: claude-sonnet-4.6
              reasoning_effort: low
            composer:
              model: copilot/claude-sonnet-4.6
              reasoning_effort: medium
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.Equal("high", config.Workers["coder"].ReasoningEffort);
        Assert.Equal("extra_high", config.Workers["coder"].PremiumReasoningEffort);
        Assert.Equal("medium", config.Workers["tester"].ReasoningEffort);
        Assert.Null(config.Workers["tester"].PremiumReasoningEffort);
        Assert.Equal("low", config.Orchestrator.ReasoningEffort);
        Assert.Equal("medium", config.Composer!.ReasoningEffort);
    }

    [Fact]
    public void Deserialize_ReasoningEffortKeysAbsent_AllNewFieldsDefaultToNull()
    {
        const string yaml = """
            version: "1.0"
            workers:
              coder:
                model: claude-opus-4.6
              tester:
                model: claude-sonnet-4.6
            orchestrator:
              model: claude-sonnet-4.6
            composer:
              model: copilot/claude-sonnet-4.6
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        // No reasoning_effort / premium_reasoning_effort keys present anywhere —
        // every new field must stay null rather than picking up a default.
        Assert.Null(config.Workers["coder"].ReasoningEffort);
        Assert.Null(config.Workers["coder"].PremiumReasoningEffort);
        Assert.Null(config.Workers["tester"].ReasoningEffort);
        Assert.Null(config.Workers["tester"].PremiumReasoningEffort);
        Assert.Null(config.Orchestrator.ReasoningEffort);
        Assert.Null(config.Composer!.ReasoningEffort);
    }

    [Fact]
    public async Task RoundTrip_ReasoningEffortFields_SurviveSerializeAndDeserialize()
    {
        var original = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "orch-model", ReasoningEffort = "high" },
            Workers =
            {
                ["coder"] = new WorkerConfig
                {
                    Model = "coder-model",
                    ReasoningEffort = "medium",
                    PremiumModel = "coder-premium",
                    PremiumReasoningEffort = "extra_high"
                }
            },
            Composer = new ComposerConfig { Model = "composer-model", ReasoningEffort = "low" }
        };

        var yaml = await WriteThroughProductionSerializerAsync(original);

        Assert.Contains("reasoning_effort: high", yaml, StringComparison.Ordinal);
        Assert.Contains("premium_reasoning_effort: extra_high", yaml, StringComparison.Ordinal);

        var reloaded = Deserializer.Deserialize<HiveConfigFile>(yaml);
        Assert.Equal("high", reloaded.Orchestrator.ReasoningEffort);
        Assert.Equal("medium", reloaded.Workers["coder"].ReasoningEffort);
        Assert.Equal("extra_high", reloaded.Workers["coder"].PremiumReasoningEffort);
        Assert.Equal("low", reloaded.Composer!.ReasoningEffort);
    }

    // ── ValidateReasoningEffort ────────────────────────────────────────────────

    /// <summary>Builds a config where every model assignment carries a valid reasoning effort.</summary>
    private static HiveConfigFile ValidReasoningConfig() => new()
    {
        Orchestrator = new OrchestratorConfig { Model = "orch-model", ReasoningEffort = "high" },
        Workers =
        {
            ["coder"] = new WorkerConfig
            {
                Model = "coder-model",
                ReasoningEffort = "medium",
                PremiumModel = "coder-premium",
                PremiumReasoningEffort = "extra_high"
            },
            ["tester"] = new WorkerConfig { Model = "tester-model", ReasoningEffort = "low" }
        },
        Composer = new ComposerConfig { Model = "composer-model", ReasoningEffort = "none" },
        Models = new ModelsConfig
        {
            // composer-model must be in the global catalog for the composer's effective
            // default to resolve (which makes composer.reasoning_effort required).
            AvailableModels = [new ModelEntry { Name = "composer-model" }],
            SubAgentModels = [new ModelEntry { Name = "sub-a", ReasoningEffort = "high" }]
        }
    };

    [Fact]
    public void ValidateReasoningEffort_FullyValidConfig_ReturnsEmptyList()
    {
        var errors = ValidReasoningConfig().ValidateReasoningEffort();

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("extra_high")]
    [InlineData("HIGH")]
    [InlineData("  high  ")]
    public void ValidateReasoningEffort_AcceptsKnownLevels_CaseInsensitiveAndTrimmed(string level)
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "orch-model", ReasoningEffort = level }
        };

        Assert.Empty(config.ValidateReasoningEffort());
        // Trimming is for comparison only — the stored value must be unchanged.
        Assert.Equal(level, config.Orchestrator.ReasoningEffort);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateReasoningEffort_MissingOrchestratorReasoning_ReturnsError(string? value)
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "orch-model", ReasoningEffort = value }
        };

        var errors = config.ValidateReasoningEffort();

        var error = Assert.Single(errors);
        Assert.Contains("orchestrator", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An UNSET orchestrator model (null/blank — Slice 3a normalizes blank to null at parse
    /// time) is its own unconfigured state: NO orchestrator reasoning error is produced, even
    /// when reasoning_effort is also missing.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateReasoningEffort_OrchestratorModelUnset_NoReasoningRequired(string? model)
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = model, ReasoningEffort = null }
        };

        Assert.Empty(config.ValidateReasoningEffort());
    }

    [Fact]
    public void ValidateReasoningEffort_InvalidLevel_ReturnsError()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "orch-model", ReasoningEffort = "ultra" }
        };

        var error = Assert.Single(config.ValidateReasoningEffort());
        Assert.Contains("ultra", error, StringComparison.Ordinal);
        Assert.Contains("orchestrator", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateReasoningEffort_WorkerModelSetWithoutReasoning_ReturnsError()
    {
        var config = ValidReasoningConfig();
        config.Workers["coder"].ReasoningEffort = null;

        var error = Assert.Single(config.ValidateReasoningEffort());
        Assert.Contains("workers.coder", error, StringComparison.Ordinal);
        Assert.DoesNotContain("premium", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateReasoningEffort_WorkerWithoutModel_NoReasoningRequired()
    {
        var config = ValidReasoningConfig();
        config.Workers["tester"] = new WorkerConfig { Model = null, ReasoningEffort = null };

        Assert.Empty(config.ValidateReasoningEffort());
    }

    [Fact]
    public void ValidateReasoningEffort_PremiumModelSetWithoutPremiumReasoning_ReturnsError()
    {
        var config = ValidReasoningConfig();
        config.Workers["coder"].PremiumReasoningEffort = null;

        var error = Assert.Single(config.ValidateReasoningEffort());
        Assert.Contains("workers.coder.premium_reasoning_effort", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateReasoningEffort_NoPremiumModel_PremiumReasoningNotRequired()
    {
        var config = ValidReasoningConfig();
        config.Workers["coder"].PremiumModel = null;
        config.Workers["coder"].PremiumReasoningEffort = null;

        Assert.Empty(config.ValidateReasoningEffort());
    }

    [Fact]
    public void ValidateReasoningEffort_ComposerModelSetWithoutReasoning_ReturnsError()
    {
        var config = ValidReasoningConfig();
        config.Composer!.ReasoningEffort = null;

        var error = Assert.Single(config.ValidateReasoningEffort());
        Assert.Contains("composer", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateReasoningEffort_ComposerWithoutModel_NoReasoningRequired()
    {
        var config = ValidReasoningConfig();
        config.Composer = new ComposerConfig { Model = null, ReasoningEffort = null };

        Assert.Empty(config.ValidateReasoningEffort());
    }

    [Fact]
    public void ValidateReasoningEffort_SubAgentModelMissingReasoning_ReturnsErrorPerEntry()
    {
        var config = ValidReasoningConfig();
        config.Models!.SubAgentModels =
        [
            new ModelEntry { Name = "sub-a", ReasoningEffort = "high" },
            new ModelEntry { Name = "sub-b", ReasoningEffort = null },
            new ModelEntry { Name = "sub-c", ReasoningEffort = "bogus" }
        ];

        var errors = config.ValidateReasoningEffort();

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("sub-b", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("sub-c", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateReasoningEffort_DoesNotValidateCompactionOrAvailableModelsList()
    {
        var config = ValidReasoningConfig();
        config.Models!.CompactionModel = "compaction-model";
        config.Models.AvailableModels =
        [
            new ModelEntry { Name = "available-a", ReasoningEffort = null },
            new ModelEntry { Name = "available-b", ReasoningEffort = "bogus" }
        ];
        config.Composer!.Model = "composer-model";

        Assert.Empty(config.ValidateReasoningEffort());
    }

    [Fact]
    public void ValidateReasoningEffort_AggregatesAllErrors_AndDoesNotThrow()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "orch-model", ReasoningEffort = null },
            Workers =
            {
                ["coder"] = new WorkerConfig { Model = "m", PremiumModel = "pm" }
            },
            Composer = new ComposerConfig { Model = "composer-model" },
            Models = new ModelsConfig
            {
                SubAgentModels = [new ModelEntry { Name = "sub-a" }]
            }
        };

        // Must return a list rather than throwing, even when errors exist.
        var errors = config.ValidateReasoningEffort();

        // 4 errors: orchestrator + coder model + coder premium + sub-a.
        // The composer does NOT contribute: its model is set but absent from the global
        // catalog (no AvailableModels here), so no effective default ⇒ no required effort.
        Assert.Equal(4, errors.Count);
    }

    // ── Composer reasoning conditioning (Slice 1A1) ───────────────────────────

    /// <summary>
    /// A set-but-absent composer.model (present in config but NOT in the global
    /// available_models catalog) with no composer.reasoning_effort does NOT fail validation.
    /// </summary>
    [Fact]
    public void ValidateReasoningEffort_ComposerModelSetButAbsentFromCatalog_NoReasoningRequired()
    {
        var config = ValidReasoningConfig();
        config.Models!.AvailableModels = [new ModelEntry { Name = "other-model" }];
        config.Composer!.ReasoningEffort = null;

        Assert.Empty(config.ValidateReasoningEffort());
    }

    /// <summary>
    /// A set-but-absent composer.model with no composer.reasoning_effort and no global catalog
    /// does NOT fail validation.
    /// </summary>
    [Fact]
    public void ValidateReasoningEffort_ComposerModelSetButNoGlobalCatalog_NoReasoningRequired()
    {
        var config = ValidReasoningConfig();
        config.Models!.AvailableModels = null;
        config.Composer!.ReasoningEffort = null;

        Assert.Empty(config.ValidateReasoningEffort());
    }

    /// <summary>
    /// When the composer model DOES resolve to a valid effective default in the global catalog,
    /// composer.reasoning_effort is still required.
    /// </summary>
    [Fact]
    public void ValidateReasoningEffort_ComposerEffectiveDefaultResolves_ReasoningStillRequired()
    {
        var config = ValidReasoningConfig();
        config.Composer!.ReasoningEffort = null;

        var error = Assert.Single(config.ValidateReasoningEffort());
        Assert.Contains("composer", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A whitespace-bearing composer model that normalizes into the catalog still requires
    /// composer.reasoning_effort.
    /// </summary>
    [Fact]
    public void ValidateReasoningEffort_ComposerModelTrimmedIntoCatalog_ReasoningRequired()
    {
        var config = ValidReasoningConfig();
        config.Composer!.Model = "  COMPOSER-MODEL  ";
        config.Composer!.ReasoningEffort = null;

        var error = Assert.Single(config.ValidateReasoningEffort());
        Assert.Contains("composer", error, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Orchestrator/role reasoning requirements: orchestrator.reasoning_effort is required
    /// when orchestrator.model is SET, and per-role model assignments still require their
    /// effort. (An unset orchestrator model produces NO orchestrator reasoning error — see
    /// <see cref="ValidateReasoningEffort_OrchestratorModelUnset_NoReasoningRequired"/>.)
    /// </summary>
    [Fact]
    public void ValidateReasoningEffort_OrchestratorAndRoleRequirements_Unchanged()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "orch-model", ReasoningEffort = null },
            Workers =
            {
                ["coder"] = new WorkerConfig { Model = "coder-model" }
            }
        };

        var errors = config.ValidateReasoningEffort();

        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("orchestrator", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(errors, e => e.Contains("workers.coder", StringComparison.Ordinal));
    }

    // ── GetSubAgentModels: duplicate available_models names ────────────────────

    [Fact]
    public void GetSubAgentModels_DuplicateAvailableNamesDifferingByCase_DoesNotThrow_LastWins()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "dup-model", ContextWindow = 1000, Description = "first" },
                    new ModelEntry { Name = "DUP-MODEL", ContextWindow = 2000, Description = "second" }
                ],
                SubAgentModels = [new ModelEntry { Name = "dup-model", ReasoningEffort = "high" }]
            }
        };

        var result = Assert.Single(config.GetSubAgentModels());

        Assert.Equal(2000, result.ContextWindow);
        Assert.Equal("second", result.Description);
    }

    // ── ReloadFrom: new fields and WorkerTaskTimeoutMinutes ────────────────────

    [Fact]
    public void ReloadFrom_CopiesReasoningEffortFieldsAndWorkerTaskTimeout()
    {
        var source = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig
            {
                Model = "orch-model",
                ReasoningEffort = "extra_high",
                WorkerTaskTimeoutMinutes = 77
            },
            Workers =
            {
                ["coder"] = new WorkerConfig
                {
                    Model = "coder-model",
                    ReasoningEffort = "high",
                    PremiumModel = "coder-premium",
                    PremiumReasoningEffort = "medium",
                    ContextWindow = 150000
                }
            },
            Composer = new ComposerConfig { Model = "composer-model", ReasoningEffort = "low" }
        };

        var target = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        target.ReloadFrom(source);

        Assert.Equal("extra_high", target.Orchestrator.ReasoningEffort);
        Assert.Equal(77, target.Orchestrator.WorkerTaskTimeoutMinutes);

        var coder = target.Workers["coder"];
        Assert.NotSame(source.Workers["coder"], coder);
        Assert.Equal("high", coder.ReasoningEffort);
        Assert.Equal("medium", coder.PremiumReasoningEffort);
        Assert.Equal(150000, coder.ContextWindow);

        Assert.NotSame(source.Composer, target.Composer);
        Assert.Equal("low", target.Composer!.ReasoningEffort);
    }

    // ── CI monitoring: YAML round-trip and ReloadFrom ─────────────────────────

    /// <summary>
    /// The same serializer configuration used by production code in
    /// <see cref="ConfigRepoManager"/> — underscored naming convention with
    /// <c>OmitDefaults | OmitNull</c>.
    /// </summary>
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitNull)
        .Build();

    [Fact]
    public void RoundTrip_CiMonitoring_NonDefaultValues_SurviveSerializeAndDeserialize()
    {
        var original = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "ci-repo",
                    Url = "https://github.com/org/ci-repo.git",
                    DefaultBranch = "main",
                    MonitorCi = true,
                    CiTimeoutMinutes = 45
                }
            ]
        };

        var yaml = Serializer.Serialize(original);

        // Non-default values must be emitted.
        Assert.Contains("monitor_ci: true", yaml, StringComparison.Ordinal);
        Assert.Contains("ci_timeout_minutes: 45", yaml, StringComparison.Ordinal);

        var reloaded = Deserializer.Deserialize<HiveConfigFile>(yaml);
        var repo = Assert.Single(reloaded.Repositories);
        Assert.True(repo.MonitorCi);
        Assert.Equal(45, repo.CiTimeoutMinutes);
    }

    [Fact]
    public void RoundTrip_CiMonitoring_Defaults_OmittedOrSerializedPerClrDefault()
    {
        // MonitorCi = false is the CLR bool default → omitted from YAML.
        // CiTimeoutMinutes = 30 is NOT the CLR int default (0) → serialized explicitly.
        var original = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "default-repo",
                    Url = "https://github.com/org/default-repo.git",
                    DefaultBranch = "main",
                    MonitorCi = false,
                    CiTimeoutMinutes = 30
                }
            ]
        };

        var yaml = Serializer.Serialize(original);

        Assert.DoesNotContain("monitor_ci", yaml, StringComparison.Ordinal);
        Assert.Contains("ci_timeout_minutes: 30", yaml, StringComparison.Ordinal);

        var reloaded = Deserializer.Deserialize<HiveConfigFile>(yaml);
        var repo = Assert.Single(reloaded.Repositories);
        Assert.False(repo.MonitorCi);
        Assert.Equal(30, repo.CiTimeoutMinutes);
    }

    [Fact]
    public void Deserialize_CiMonitoringKeysAbsent_UsesPropertyDefaults()
    {
        const string yaml = """
            version: "1.0"
            repositories:
              - name: plain-repo
                url: https://github.com/org/plain-repo.git
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        var repo = Assert.Single(config.Repositories);
        Assert.False(repo.MonitorCi);
        Assert.Equal(30, repo.CiTimeoutMinutes);
    }

    [Fact]
    public void ReloadFrom_CopiesCiMonitoringFields()
    {
        var source = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "ci-repo",
                    Url = "https://github.com/org/ci-repo.git",
                    DefaultBranch = "main",
                    MonitorCi = true,
                    CiTimeoutMinutes = 45
                }
            ]
        };

        var target = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        target.ReloadFrom(source);

        var repo = Assert.Single(target.Repositories);
        Assert.NotSame(source.Repositories[0], repo);
        Assert.True(repo.MonitorCi);
        Assert.Equal(45, repo.CiTimeoutMinutes);

        // Mutating the source after ReloadFrom must not affect the receiver.
        source.Repositories[0].MonitorCi = false;
        source.Repositories[0].CiTimeoutMinutes = 10;
        Assert.True(repo.MonitorCi);
        Assert.Equal(45, repo.CiTimeoutMinutes);
    }

    // ── EventNotificationsConfig: modes, whitelist, throttle, YAML round-trip ────

    [Theory]
    [InlineData("passive", "passive")]
    [InlineData("active", "active")]
    [InlineData("off", "off")]
    [InlineData("PASSIVE", "passive")]
    [InlineData("  Active  ", "active")]
    [InlineData(null, "passive")]
    [InlineData("", "passive")]
    [InlineData("   ", "passive")]
    [InlineData("bogus", "passive")]
    public void EventNotificationsConfig_EffectiveMode_NormalizesMode(string? mode, string expected)
    {
        var config = new EventNotificationsConfig { Mode = mode };

        Assert.Equal(expected, config.EffectiveMode);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(300, 300)]
    [InlineData(301, 300)]
    [InlineData(-5, 1)]
    public void EventNotificationsConfig_EffectiveThrottleSeconds_ClampsToRange(int? seconds, int expected)
    {
        var config = new EventNotificationsConfig { ThrottleSeconds = seconds };

        Assert.Equal(expected, config.EffectiveThrottleSeconds);
    }

    [Fact]
    public void EventNotificationsConfig_GetActiveEventTypes_NullList_ReturnsAllFourWhitelisted()
    {
        var config = new EventNotificationsConfig { ActiveEvents = null };

        var types = config.GetActiveEventTypes();

        Assert.Equal(4, types.Count);
        Assert.Contains(EventType.GoalCompleted, types);
        Assert.Contains(EventType.GoalFailed, types);
        Assert.Contains(EventType.CiFailed, types);
        Assert.Contains(EventType.IssueRaised, types);
    }

    [Fact]
    public void EventNotificationsConfig_GetActiveEventTypes_EmptyList_ReturnsAllFourWhitelisted()
    {
        var config = new EventNotificationsConfig { ActiveEvents = [] };

        var types = config.GetActiveEventTypes();

        Assert.Equal(4, types.Count);
        Assert.Contains(EventType.GoalCompleted, types);
        Assert.Contains(EventType.GoalFailed, types);
        Assert.Contains(EventType.CiFailed, types);
        Assert.Contains(EventType.IssueRaised, types);
    }

    [Fact]
    public void EventNotificationsConfig_GetActiveEventTypes_InvalidOnly_DefaultsToAllFour()
    {
        var config = new EventNotificationsConfig { ActiveEvents = ["not_an_event", "also_invalid"] };

        var types = config.GetActiveEventTypes();

        Assert.Equal(4, types.Count);
        Assert.Contains(EventType.GoalCompleted, types);
        Assert.Contains(EventType.GoalFailed, types);
        Assert.Contains(EventType.CiFailed, types);
        Assert.Contains(EventType.IssueRaised, types);
    }

    [Fact]
    public void EventNotificationsConfig_GetActiveEventTypes_ParsesSnakeCaseNames()
    {
        var config = new EventNotificationsConfig
        {
            ActiveEvents = ["goal_completed", "ci_failed"]
        };

        var types = config.GetActiveEventTypes();

        Assert.Equal(2, types.Count);
        Assert.Contains(EventType.GoalCompleted, types);
        Assert.Contains(EventType.CiFailed, types);
    }

    [Fact]
    public void EventNotificationsConfig_GetActiveEventTypes_IgnoresNonWhitelistedValidParses()
    {
        // package_publish_timed_out parses but is NOT in the whitelist.
        var config = new EventNotificationsConfig
        {
            ActiveEvents = ["goal_completed", "package_publish_timed_out"]
        };

        var types = config.GetActiveEventTypes();

        Assert.Single(types);
        Assert.Contains(EventType.GoalCompleted, types);
    }

    [Fact]
    public void EventNotificationsConfig_GetInvalidEventNames_ReturnsUnparseableAndNonWhitelisted()
    {
        var config = new EventNotificationsConfig
        {
            ActiveEvents = ["goal_completed", "not_an_event", "package_publish_timed_out", "123", "a,b"]
        };

        var invalid = config.GetInvalidEventNames();

        Assert.Equal(4, invalid.Count);
        Assert.Contains("not_an_event", invalid);
        Assert.Contains("package_publish_timed_out", invalid);
        Assert.Contains("123", invalid);
        Assert.Contains("a,b", invalid);
        Assert.DoesNotContain("goal_completed", invalid);
    }

    [Fact]
    public void EventNotificationsConfig_GetInvalidEventNames_NullList_ReturnsEmpty()
    {
        var config = new EventNotificationsConfig { ActiveEvents = null };

        Assert.Empty(config.GetInvalidEventNames());
    }

    [Fact]
    public void EventNotificationsConfig_GetActiveEventTypes_MixedValidInvalid_FiltersToWhitelisted()
    {
        var config = new EventNotificationsConfig
        {
            ActiveEvents = ["goal_completed", "bogus_event", "issue_raised"]
        };

        var types = config.GetActiveEventTypes();

        Assert.Equal(2, types.Count);
        Assert.Contains(EventType.GoalCompleted, types);
        Assert.Contains(EventType.IssueRaised, types);
    }

    [Fact]
    public void EventNotificationsConfig_GetActiveEventTypes_ReturnsFreshSet_NotSharedReference()
    {
        var config = new EventNotificationsConfig();

        var first = config.GetActiveEventTypes();
        var second = config.GetActiveEventTypes();

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Deserialize_EventNotificationsSection_PopulatesFields()
    {
        const string yaml = """
            version: "1.0"
            composer:
              model: copilot/composer-model
              event_notifications:
                mode: active
                active_events:
                  - goal_completed
                  - ci_failed
                throttle_seconds: 60
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config.Composer);
        Assert.NotNull(config.Composer!.EventNotifications);
        Assert.Equal("active", config.Composer.EventNotifications!.Mode);
        Assert.Equal(2, config.Composer.EventNotifications.ActiveEvents!.Count);
        Assert.Equal("goal_completed", config.Composer.EventNotifications.ActiveEvents[0]);
        Assert.Equal("ci_failed", config.Composer.EventNotifications.ActiveEvents[1]);
        Assert.Equal(60, config.Composer.EventNotifications.ThrottleSeconds);
        Assert.Equal("active", config.Composer.EventNotifications.EffectiveMode);
    }

    [Fact]
    public void Deserialize_NoEventNotificationsSection_EventNotificationsIsNull()
    {
        const string yaml = """
            version: "1.0"
            composer:
              model: copilot/composer-model
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config.Composer);
        Assert.Null(config.Composer!.EventNotifications);
    }

    [Fact]
    public void RoundTrip_EventNotifications_NullYieldsNoKey()
    {
        var original = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { Model = "composer-model" }
        };

        var yaml = Serializer.Serialize(original);

        Assert.DoesNotContain("event_notifications", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTrip_EventNotifications_OnlyYamlFieldsSerialized()
    {
        var original = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig
            {
                Model = "composer-model",
                EventNotifications = new EventNotificationsConfig
                {
                    Mode = "active",
                    ActiveEvents = ["goal_completed", "issue_raised"],
                    ThrottleSeconds = 45
                }
            }
        };

        var yaml = Serializer.Serialize(original);

        Assert.Contains("event_notifications:", yaml, StringComparison.Ordinal);
        Assert.Contains("mode: active", yaml, StringComparison.Ordinal);
        Assert.Contains("active_events:", yaml, StringComparison.Ordinal);
        Assert.Contains("goal_completed", yaml, StringComparison.Ordinal);
        Assert.Contains("issue_raised", yaml, StringComparison.Ordinal);
        Assert.Contains("throttle_seconds: 45", yaml, StringComparison.Ordinal);

        // Computed properties must NOT be serialized.
        Assert.DoesNotContain("effective_mode", yaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("effective_throttle", yaml, StringComparison.OrdinalIgnoreCase);

        var reloaded = Deserializer.Deserialize<HiveConfigFile>(yaml);
        var notif = reloaded.Composer!.EventNotifications!;
        Assert.Equal("active", notif.Mode);
        Assert.Equal(2, notif.ActiveEvents!.Count);
        Assert.Equal("goal_completed", notif.ActiveEvents[0]);
        Assert.Equal("issue_raised", notif.ActiveEvents[1]);
        Assert.Equal(45, notif.ThrottleSeconds);
    }

    [Fact]
    public void ReloadFrom_CopiesEventNotifications_DeepCopiesActiveEvents()
    {
        var source = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig
            {
                Model = "composer-model",
                EventNotifications = new EventNotificationsConfig
                {
                    Mode = "active",
                    ActiveEvents = ["goal_completed"],
                    ThrottleSeconds = 15
                }
            }
        };

        var target = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        target.ReloadFrom(source);

        Assert.NotNull(target.Composer);
        Assert.NotNull(target.Composer!.EventNotifications);
        Assert.NotSame(source.Composer.EventNotifications, target.Composer.EventNotifications);
        Assert.NotSame(source.Composer.EventNotifications.ActiveEvents, target.Composer.EventNotifications.ActiveEvents);
        Assert.Equal("active", target.Composer.EventNotifications!.Mode);
        Assert.Equal(["goal_completed"], target.Composer.EventNotifications.ActiveEvents);
        Assert.Equal(15, target.Composer.EventNotifications.ThrottleSeconds);

        // Mutating source must not affect receiver.
        source.Composer.EventNotifications.ActiveEvents!.Add("ci_failed");
        source.Composer.EventNotifications.Mode = "off";

        Assert.Single(target.Composer.EventNotifications.ActiveEvents!);
        Assert.Equal("active", target.Composer.EventNotifications.Mode);
    }

    [Fact]
    public void ReloadFrom_NullEventNotifications_ReceiverBecomesNull()
    {
        var receiver = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig
            {
                Model = "old-composer",
                EventNotifications = new EventNotificationsConfig { Mode = "active" }
            }
        };

        var source = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { Model = "new-composer", EventNotifications = null }
        };

        receiver.ReloadFrom(source);

        Assert.NotNull(receiver.Composer);
        Assert.Null(receiver.Composer!.EventNotifications);
    }

    // ── NuGet publish config: YAML round-trip and ReloadFrom deep-copy ─────────

    [Fact]
    public async Task RoundTrip_PublishNuGet_SurvivesSerializeAndDeserialize()
    {
        var original = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "nuget-repo",
                    Url = "https://github.com/org/nuget-repo.git",
                    DefaultBranch = "main",
                    PublishNuGet = new NuGetPublishConfig
                    {
                        Packages =
                        [
                            new NuGetPackageEntry { PackageId = "My.Library" },
                            new NuGetPackageEntry { PackageId = "My.Tools" }
                        ]
                    }
                }
            ]
        };

        var yaml = Serializer.Serialize(original);

        Assert.Contains("publish_nuget:", yaml, StringComparison.Ordinal);
        Assert.Contains("package_id: My.Library", yaml, StringComparison.Ordinal);
        Assert.Contains("package_id: My.Tools", yaml, StringComparison.Ordinal);

        var reloaded = Deserializer.Deserialize<HiveConfigFile>(yaml);
        var repo = Assert.Single(reloaded.Repositories);
        Assert.NotNull(repo.PublishNuGet);
        Assert.Equal(2, repo.PublishNuGet!.Packages.Count);
        Assert.Equal("My.Library", repo.PublishNuGet.Packages[0].PackageId);
        Assert.Equal("My.Tools", repo.PublishNuGet.Packages[1].PackageId);
    }

    [Fact]
    public void ReloadFrom_PublishNuGet_DeepCopiesListAndItems()
    {
        var source = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "repo",
                    Url = "https://github.com/org/repo.git",
                    DefaultBranch = "main",
                    PublishNuGet = new NuGetPublishConfig
                    {
                        Packages = [new NuGetPackageEntry { PackageId = "Orig.Package" }]
                    }
                }
            ]
        };

        var receiver = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        receiver.ReloadFrom(source);

        var copied = Assert.Single(receiver.Repositories);
        Assert.NotNull(copied.PublishNuGet);
        Assert.NotSame(source.Repositories[0].PublishNuGet, copied.PublishNuGet);
        Assert.NotSame(source.Repositories[0].PublishNuGet!.Packages, copied.PublishNuGet!.Packages);
        var copiedEntry = Assert.Single(copied.PublishNuGet.Packages);
        Assert.NotSame(source.Repositories[0].PublishNuGet!.Packages[0], copiedEntry);
        Assert.Equal("Orig.Package", copiedEntry.PackageId);
    }

    [Fact]
    public void ReloadFrom_PublishNuGet_MutatingSourceDoesNotAffectReceiver()
    {
        var sourceEntry = new NuGetPackageEntry { PackageId = "Orig.Package" };
        var source = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "repo",
                    Url = "https://github.com/org/repo.git",
                    DefaultBranch = "main",
                    PublishNuGet = new NuGetPublishConfig { Packages = [sourceEntry] }
                }
            ]
        };

        var receiver = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        receiver.ReloadFrom(source);

        // Mutate the source after reload.
        sourceEntry.PackageId = "Mutated.Package";
        source.Repositories[0].PublishNuGet!.Packages.Add(new NuGetPackageEntry { PackageId = "Added.Package" });

        var copied = Assert.Single(receiver.Repositories);
        var copiedEntry = Assert.Single(copied.PublishNuGet!.Packages);
        Assert.Equal("Orig.Package", copiedEntry.PackageId);
    }

    [Fact]
    public void ReloadFrom_NullPublishNuGet_ReceiverBecomesNull()
    {
        var receiver = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "repo",
                    Url = "https://github.com/org/repo.git",
                    DefaultBranch = "main",
                    PublishNuGet = new NuGetPublishConfig { Packages = [new NuGetPackageEntry { PackageId = "Old.Package" }] }
                }
            ]
        };

        var source = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "repo",
                    Url = "https://github.com/org/repo.git",
                    DefaultBranch = "main",
                    PublishNuGet = null
                }
            ]
        };

        receiver.ReloadFrom(source);

        Assert.Null(receiver.Repositories[0].PublishNuGet);
    }

    // ── package_published: recognized but not default ──────────────────────────

    [Fact]
    public void EventNotificationsConfig_PackagePublished_RecognizedButNotDefault()
    {
        var config = new EventNotificationsConfig { ActiveEvents = null };

        var types = config.GetActiveEventTypes();

        // Default = 4: package_published must NOT be active by default.
        Assert.Equal(4, types.Count);
        Assert.DoesNotContain(EventType.PackagePublished, types);
        Assert.Contains(EventType.GoalCompleted, types);
        Assert.Contains(EventType.GoalFailed, types);
        Assert.Contains(EventType.CiFailed, types);
        Assert.Contains(EventType.IssueRaised, types);
    }

    [Fact]
    public void EventNotificationsConfig_PackagePublished_ExplicitList_IsActive()
    {
        var config = new EventNotificationsConfig
        {
            ActiveEvents = ["package_published"]
        };

        var types = config.GetActiveEventTypes();

        Assert.Single(types);
        Assert.Contains(EventType.PackagePublished, types);
    }

    [Fact]
    public void Deserialize_ActiveEventsWithPackagePublished_Accepted()
    {
        const string yaml = """
            version: "1.0"
            composer:
              model: copilot/composer-model
              event_notifications:
                mode: active
                active_events:
                  - package_published
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config.Composer!.EventNotifications);
        Assert.Equal(["package_published"], config.Composer.EventNotifications.ActiveEvents);
        var types = config.Composer.EventNotifications.GetActiveEventTypes();
        Assert.Single(types);
        Assert.Contains(EventType.PackagePublished, types);
    }

    [Fact]
    public void Deserialize_ActiveEventsWithRoutineEvents_AllNineAccepted()
    {
        const string yaml = """
            version: "1.0"
            composer:
              model: copilot/composer-model
              event_notifications:
                mode: active
                active_events:
                  - goal_completed
                  - goal_failed
                  - ci_failed
                  - issue_raised
                  - package_published
                  - ci_succeeded
                  - release_completed
                  - goal_dispatched
                  - issue_resolved
            """;

        var config = Deserializer.Deserialize<HiveConfigFile>(yaml);

        Assert.NotNull(config.Composer!.EventNotifications);
        Assert.Equal(
            ["goal_completed", "goal_failed", "ci_failed", "issue_raised", "package_published",
             "ci_succeeded", "release_completed", "goal_dispatched", "issue_resolved"],
            config.Composer.EventNotifications.ActiveEvents);
        var types = config.Composer.EventNotifications.GetActiveEventTypes();
        Assert.Equal(9, types.Count);
        Assert.Contains(EventType.CiSucceeded, types);
        Assert.Contains(EventType.ReleaseCompleted, types);
        Assert.Contains(EventType.GoalDispatched, types);
        Assert.Contains(EventType.IssueResolved, types);
        Assert.Empty(config.Composer.EventNotifications.GetInvalidEventNames());
    }

    // ── Additional integration coverage for NuGet publish config ────────────────

    [Fact]
    public void ReloadFrom_PublishNuGet_MutatingReceiverDoesNotAffectSource()
    {
        var source = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "repo",
                    Url = "https://github.com/org/repo.git",
                    DefaultBranch = "main",
                    PublishNuGet = new NuGetPublishConfig
                    {
                        Packages = [new NuGetPackageEntry { PackageId = "Orig.Package" }]
                    }
                }
            ]
        };

        var receiver = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        receiver.ReloadFrom(source);

        // Mutate the receiver after reload.
        var copied = Assert.Single(receiver.Repositories);
        copied.PublishNuGet!.Packages[0].PackageId = "Changed.Package";
        copied.PublishNuGet.Packages.Add(new NuGetPackageEntry { PackageId = "Extra.Package" });

        // Source must be unaffected.
        var sourceRepo = Assert.Single(source.Repositories);
        var sourceEntry = Assert.Single(sourceRepo.PublishNuGet!.Packages);
        Assert.Equal("Orig.Package", sourceEntry.PackageId);
    }

    [Fact]
    public void ReloadFrom_PublishNuGet_EmptyPackagesList_CopiedAsEmpty()
    {
        var source = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "repo",
                    Url = "https://github.com/org/repo.git",
                    DefaultBranch = "main",
                    PublishNuGet = new NuGetPublishConfig { Packages = [] }
                }
            ]
        };

        var receiver = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        receiver.ReloadFrom(source);

        var copied = Assert.Single(receiver.Repositories);
        Assert.NotNull(copied.PublishNuGet);
        Assert.Empty(copied.PublishNuGet!.Packages);
        Assert.NotSame(source.Repositories[0].PublishNuGet, copied.PublishNuGet);
        Assert.NotSame(source.Repositories[0].PublishNuGet!.Packages, copied.PublishNuGet.Packages);
    }

    [Fact]
    public async Task RoundTrip_PublishNuGet_EmptyPackagesList_SurvivesSerializeDeserialize()
    {
        var original = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "nuget-repo",
                    Url = "https://github.com/org/nuget-repo.git",
                    DefaultBranch = "main",
                    PublishNuGet = new NuGetPublishConfig { Packages = [] }
                }
            ]
        };

        var yaml = Serializer.Serialize(original);
        Assert.Contains("publish_nuget:", yaml, StringComparison.Ordinal);

        var reloaded = Deserializer.Deserialize<HiveConfigFile>(yaml);
        var repo = Assert.Single(reloaded.Repositories);
        Assert.NotNull(repo.PublishNuGet);
        Assert.Empty(repo.PublishNuGet!.Packages);
    }

    [Fact]
    public async Task RoundTrip_PublishNuGet_NullPublishNuGet_NotSerialized()
    {
        var original = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "nuget-repo",
                    Url = "https://github.com/org/nuget-repo.git",
                    DefaultBranch = "main",
                    PublishNuGet = null
                }
            ]
        };

        var yaml = Serializer.Serialize(original);
        Assert.DoesNotContain("publish_nuget", yaml, StringComparison.Ordinal);

        var reloaded = Deserializer.Deserialize<HiveConfigFile>(yaml);
        var repo = Assert.Single(reloaded.Repositories);
        Assert.Null(repo.PublishNuGet);
    }

    [Fact]
    public void EventNotificationsConfig_PackagePublished_NotReportedAsInvalid()
    {
        var config = new EventNotificationsConfig
        {
            ActiveEvents = ["package_published", "goal_completed"]
        };

        var invalid = config.GetInvalidEventNames();

        Assert.Empty(invalid);
    }

    [Fact]
    public void EventNotificationsConfig_PackagePublishTimedOut_NotInRecognizedActiveEvents()
    {
        // PackagePublishTimedOut is in the EventType enum but must NOT be a recognized active event.
        // It should be treated as invalid if someone tries to use it as an active event.
        var config = new EventNotificationsConfig
        {
            ActiveEvents = ["package_publish_timed_out"]
        };

        var types = config.GetActiveEventTypes();

        // Falls back to defaults because the only entry was not recognized.
        Assert.Equal(4, types.Count);
        Assert.DoesNotContain(EventType.PackagePublishTimedOut, types);

        var invalid = config.GetInvalidEventNames();
        Assert.Contains("package_publish_timed_out", invalid);
    }

    [Fact]
    public void EventNotificationsConfig_AllNineRecognized_AllAccepted()
    {
        var config = new EventNotificationsConfig
        {
            ActiveEvents = ["goal_completed", "goal_failed", "ci_failed", "issue_raised", "package_published", "ci_succeeded", "release_completed", "goal_dispatched", "issue_resolved"]
        };

        var types = config.GetActiveEventTypes();

        Assert.Equal(9, types.Count);
        Assert.Contains(EventType.GoalCompleted, types);
        Assert.Contains(EventType.GoalFailed, types);
        Assert.Contains(EventType.CiFailed, types);
        Assert.Contains(EventType.IssueRaised, types);
        Assert.Contains(EventType.PackagePublished, types);
        Assert.Contains(EventType.CiSucceeded, types);
        Assert.Contains(EventType.ReleaseCompleted, types);
        Assert.Contains(EventType.GoalDispatched, types);
        Assert.Contains(EventType.IssueResolved, types);

        // None should be reported as invalid.
        Assert.Empty(config.GetInvalidEventNames());
    }
}
