using CopilotHive.Configuration;
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
                AlwaysImprove = true,
                VerboseLogging = true,
                BrainContextWindow = 300000,
                BrainMaxSteps = 50,
                WorkerContextWindow = 180000,
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
                Models = ["copilot/composer-model", "copilot/alt-model"],
                ContextWindow = 160000,
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
        Assert.True(receiver.Orchestrator.AlwaysImprove);
        Assert.True(receiver.Orchestrator.VerboseLogging);
        Assert.Equal(300000, receiver.Orchestrator.BrainContextWindow);
        Assert.Equal(50, receiver.Orchestrator.BrainMaxSteps);
        Assert.Equal(180000, receiver.Orchestrator.WorkerContextWindow);
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
        Assert.Equal(2, receiver.Composer.Models!.Count);
        Assert.Equal("copilot/composer-model", receiver.Composer.Models[0]);
        Assert.Equal("copilot/alt-model", receiver.Composer.Models[1]);
        Assert.Equal(160000, receiver.Composer.ContextWindow);
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
                Models = ["old-composer", "old-alt"],
                ContextWindow = 50000,
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
                AlwaysImprove = false,
                VerboseLogging = false,
                BrainContextWindow = 200000,
                BrainMaxSteps = 30,
                WorkerContextWindow = 128000,
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
                Models = ["orig-composer", "orig-alt"],
                ContextWindow = 80000,
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
        var receiverComposerModelsCount = receiver.Composer!.Models!.Count;

        // Act — mutate source in every possible way
        source.Repositories.Add(new RepositoryConfig { Name = "new-repo", Url = "https://github.com/org/new", DefaultBranch = "dev" });
        source.Workers["coder"].Model = "mutated-model";
        source.Orchestrator.Model = "mutated-orchestrator";
        source.Models!.AvailableModels!.Add(new ModelEntry { Name = "new-model-entry", ContextWindow = 99999 });
        source.Composer!.Models!.Add("mutated-composer-model");

        // Assert — receiver is NOT affected by any source mutations
        Assert.Equal(receiverRepoCount, receiver.Repositories.Count);
        Assert.Equal(receiverCoderModel, receiver.Workers["coder"].Model);
        Assert.Equal(receiverOrchestratorModel, receiver.Orchestrator.Model);
        Assert.Equal(receiverModelsAvailableCount, receiver.Models!.AvailableModels!.Count);
        Assert.Equal(receiverComposerModelsCount, receiver.Composer!.Models!.Count);

        // Also verify the receiver still has the original values
        Assert.Equal("orig-repo", receiver.Repositories[0].Name);
        Assert.Equal("orig-model", receiver.Workers["coder"].Model);
        Assert.Equal("orig-orchestrator", receiver.Orchestrator.Model);
        Assert.Equal("orig-model-entry", receiver.Models.AvailableModels[0].Name);
        Assert.Equal("orig-composer", receiver.Composer.Models![0]);
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
            Repositories = [new RepositoryConfig { Name = "old-repo", Url = "https://github.com/org/old", DefaultBranch = "main" }],
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
                Models = ["old-composer"],
                ContextWindow = 40000,
                MaxSteps = 5
            }
        };

        // Capture references before reload
        var oldRepositories = receiver.Repositories;
        var oldWorkers = receiver.Workers;
        var oldAvailableModels = receiver.Models!.AvailableModels!;
        var oldComposerModels = receiver.Composer!.Models!;

        // Build a new source with different data
        var source = new HiveConfigFile
        {
            Version = "2.0",
            Repositories = [new RepositoryConfig { Name = "new-repo", Url = "https://github.com/org/new", DefaultBranch = "develop" }],
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
                AlwaysImprove = true,
                VerboseLogging = false,
                BrainContextWindow = 500000,
                BrainMaxSteps = 100,
                WorkerContextWindow = 200000,
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
                Models = ["new-composer", "new-alt"],
                ContextWindow = 150000,
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

        Assert.Single(oldWorkers);
        Assert.True(oldWorkers.ContainsKey("old-role"));
        Assert.Equal("old-model", oldWorkers["old-role"].Model);

        Assert.Single(oldAvailableModels);
        Assert.Equal("old-model-entry", oldAvailableModels[0].Name);

        Assert.Single(oldComposerModels);
        Assert.Equal("old-composer", oldComposerModels[0]);

        // Assert — receiver now references entirely new collections
        Assert.NotSame(oldRepositories, receiver.Repositories);
        Assert.NotSame(oldWorkers, receiver.Workers);
        Assert.NotSame(oldAvailableModels, receiver.Models!.AvailableModels);
        Assert.NotSame(oldComposerModels, receiver.Composer!.Models);

        // Assert — receiver's new data matches the source
        Assert.Equal("2.0", receiver.Version);
        Assert.Single(receiver.Repositories);
        Assert.Equal("new-repo", receiver.Repositories[0].Name);
        Assert.Single(receiver.Workers);
        Assert.Equal("new-model", receiver.Workers["new-role"].Model);
        Assert.Equal("new-orchestrator", receiver.Orchestrator.Model);
        Assert.Equal("new-compaction", receiver.Models.CompactionModel);
        Assert.Single(receiver.Models.AvailableModels!);
        Assert.Equal("new-model-entry", receiver.Models.AvailableModels[0].Name);
        Assert.Equal(2, receiver.Composer!.Models!.Count);
        Assert.Equal("new-composer", receiver.Composer.Models[0]);
    }

    // ── GetComposerAvailableModels tests ──────────────────────────────────────

    /// <summary>
    /// When Models.AvailableModels is populated, GetComposerAvailableModels returns
    /// the names from that list.
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

        var result = config.GetComposerAvailableModels("fallback");

        Assert.Equal(3, result.Count);
        Assert.Equal("copilot/claude-sonnet-4.6", result[0]);
        Assert.Equal("copilot/gpt-5.4-mini", result[1]);
        Assert.Equal("copilot/o3", result[2]);
    }

    /// <summary>
    /// When Models is null, GetComposerAvailableModels falls back to
    /// ComposerConfig.GetAvailableModels(fallback).
    /// </summary>
    [Fact]
    public void GetComposerAvailableModels_GlobalModelsNull_FallsBackToComposerConfig()
    {
        var config = new HiveConfigFile
        {
            Models = null,
            Composer = new ComposerConfig
            {
                Model = "composer-model",
                Models = ["composer-model", "alt-model"]
            }
        };

        var result = config.GetComposerAvailableModels("fallback");

        // ComposerConfig.GetAvailableModels returns Model first, then Models
        Assert.Equal(2, result.Count);
        Assert.Equal("composer-model", result[0]);
        Assert.Equal("alt-model", result[1]);
    }

    /// <summary>
    /// When Models.AvailableModels is an empty list, GetComposerAvailableModels
    /// falls back to ComposerConfig.GetAvailableModels(fallback).
    /// </summary>
    [Fact]
    public void GetComposerAvailableModels_GlobalAvailableModelsEmpty_FallsBackToComposerConfig()
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

        var result = config.GetComposerAvailableModels("fallback");

        // Empty AvailableModels triggers fallback to ComposerConfig
        Assert.Single(result);
        Assert.Equal("fallback-model", result[0]);
    }

    /// <summary>
    /// When both Models and Composer are null, GetComposerAvailableModels
    /// returns a list containing just the fallback parameter.
    /// </summary>
    [Fact]
    public void GetComposerAvailableModels_BothNull_ReturnsFallbackList()
    {
        var config = new HiveConfigFile
        {
            Models = null,
            Composer = null
        };

        var result = config.GetComposerAvailableModels("my-fallback-model");

        Assert.Single(result);
        Assert.Equal("my-fallback-model", result[0]);
    }

    /// <summary>
    /// When Models has AvailableModels but Composer is null, the global list
    /// is still returned (global takes precedence).
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

        var result = config.GetComposerAvailableModels("fallback");

        Assert.Single(result);
        Assert.Equal("global-only-model", result[0]);
    }
}