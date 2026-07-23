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
                Models = ["copilot/composer-model", "copilot/alt-model"],
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
        Assert.Equal(2, receiver.Composer.Models!.Count);
        Assert.Equal("copilot/composer-model", receiver.Composer.Models[0]);
        Assert.Equal("copilot/alt-model", receiver.Composer.Models[1]);
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
                Models = ["orig-composer", "orig-alt"],
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
                Models = ["old-composer"],
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
                AlwaysImprove = true,
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
                Models = ["new-composer", "new-alt"],
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

        Assert.Single(oldComposerModels);
        Assert.Equal("old-composer", oldComposerModels[0]);

        // Assert — receiver now references entirely new collections
        Assert.NotSame(oldRepositories, receiver.Repositories);
        Assert.NotSame(oldWorkers, receiver.Workers);
        Assert.NotSame(oldAvailableModels, receiver.Models!.AvailableModels);
        Assert.NotSame(oldComposerModels, receiver.Composer!.Models);

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

    // ── TryGetReasoningEffortForModel tests ──────────────────────────────────

    [Fact]
    public void TryGetReasoningEffortForModel_ModelWithReasoningEffort_ReturnsValue()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ReasoningEffort = "high" }
                ]
            }
        };

        Assert.Equal("high", config.TryGetReasoningEffortForModel("copilot/claude-sonnet-4.6"));
    }

    [Fact]
    public void TryGetReasoningEffortForModel_CaseInsensitive_ReturnsValue()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ReasoningEffort = "medium" }
                ]
            }
        };

        Assert.Equal("medium", config.TryGetReasoningEffortForModel("COPILOT/CLAUDE-SONNET-4.6"));
    }

    [Fact]
    public void TryGetReasoningEffortForModel_NullInput_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "model-a", ReasoningEffort = "high" }]
            }
        };

        Assert.Null(config.TryGetReasoningEffortForModel(null));
    }

    [Fact]
    public void TryGetReasoningEffortForModel_ModelNotFound_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "model-a", ReasoningEffort = "high" }]
            }
        };

        Assert.Null(config.TryGetReasoningEffortForModel("model-b"));
    }

    [Fact]
    public void TryGetReasoningEffortForModel_ModelsNull_ReturnsNull()
    {
        var config = new HiveConfigFile { Models = null };

        Assert.Null(config.TryGetReasoningEffortForModel("any-model"));
    }

    [Fact]
    public void TryGetReasoningEffortForModel_NoReasoningEffortSet_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "model-a", ReasoningEffort = null }]
            }
        };

        Assert.Null(config.TryGetReasoningEffortForModel("model-a"));
    }

    // ── ApplyReasoningSuffix tests ───────────────────────────────────────────

    [Fact]
    public void ApplyReasoningSuffix_WithReasoningEffort_AppendsSuffix()
    {
        var result = HiveConfigFile.ApplyReasoningSuffix("copilot/claude-sonnet-4.6", "high");

        Assert.Equal("copilot/claude-sonnet-4.6:high", result);
    }

    [Fact]
    public void ApplyReasoningSuffix_NullReasoningEffort_ReturnsUnchanged()
    {
        var result = HiveConfigFile.ApplyReasoningSuffix("copilot/claude-sonnet-4.6", null);

        Assert.Equal("copilot/claude-sonnet-4.6", result);
    }

    [Fact]
    public void ApplyReasoningSuffix_EmptyReasoningEffort_ReturnsUnchanged()
    {
        var result = HiveConfigFile.ApplyReasoningSuffix("copilot/claude-sonnet-4.6", "");

        Assert.Equal("copilot/claude-sonnet-4.6", result);
    }

    [Fact]
    public void ApplyReasoningSuffix_ModelAlreadyHasKnownSuffix_DoesNotOverride()
    {
        // Explicit :suffix takes precedence over configured reasoning effort
        var result = HiveConfigFile.ApplyReasoningSuffix("copilot/claude-sonnet-4.6:low", "high");

        Assert.Equal("copilot/claude-sonnet-4.6:low", result);
    }

    [Fact]
    public void ApplyReasoningSuffix_ModelHasUnknownSuffix_AppendsReasoning()
    {
        // A colon that is not a known reasoning level (e.g. a tag) should still get the reasoning suffix appended
        var result = HiveConfigFile.ApplyReasoningSuffix("ollama/llama3.2:latest", "high");

        Assert.Equal("ollama/llama3.2:latest:high", result);
    }

    [Theory]
    [InlineData("none")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("extra_high")]
    public void ApplyReasoningSuffix_ExistingKnownSuffix_AllLevels_NotOverridden(string level)
    {
        var model = $"copilot/some-model:{level}";

        var result = HiveConfigFile.ApplyReasoningSuffix(model, "high");

        Assert.Equal(model, result);
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

    // ── TryGetContextWindowForModel: reasoning suffix stripping ─────────────────

    /// <summary>
    /// A model name with a <c>:high</c> suffix should resolve to the same context
    /// window as the bare name — the suffix is stripped before matching.
    /// </summary>
    [Fact]
    public void TryGetContextWindowForModel_HighSuffix_ReturnsContextWindow()
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

        Assert.Equal(200_000, config.TryGetContextWindowForModel("copilot/claude-sonnet-4.6:high"));
        // Must equal the bare-name lookup
        Assert.Equal(
            config.TryGetContextWindowForModel("copilot/claude-sonnet-4.6"),
            config.TryGetContextWindowForModel("copilot/claude-sonnet-4.6:high"));
    }

    /// <summary>
    /// Every known reasoning level suffix should be stripped and the correct context
    /// window returned.
    /// </summary>
    [Theory]
    [InlineData(":none")]
    [InlineData(":low")]
    [InlineData(":medium")]
    [InlineData(":high")]
    [InlineData(":extra_high")]
    public void TryGetContextWindowForModel_AllKnownSuffixes_ReturnsContextWindow(string suffix)
    {
        const string modelName = "copilot/claude-sonnet-4.6";
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = modelName, ContextWindow = 200_000 }
                ]
            }
        };

        Assert.Equal(200_000, config.TryGetContextWindowForModel(modelName + suffix));
    }

    /// <summary>
    /// No suffix at all — existing behaviour must remain intact (regression check).
    /// </summary>
    [Fact]
    public void TryGetContextWindowForModel_NoSuffix_ReturnsContextWindow()
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
    }

    /// <summary>
    /// Case-insensitive suffix stripping — <c>:HIGH</c>, <c>:High</c>, etc. should
    /// all be stripped and resolve correctly.
    /// </summary>
    [Theory]
    [InlineData(":HIGH")]
    [InlineData(":High")]
    public void TryGetContextWindowForModel_CaseInsensitiveSuffix_ReturnsContextWindow(string suffix)
    {
        const string modelName = "copilot/claude-sonnet-4.6";
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = modelName, ContextWindow = 200_000 }
                ]
            }
        };

        Assert.Equal(200_000, config.TryGetContextWindowForModel(modelName + suffix));
    }

    /// <summary>
    /// A non-reasoning colon suffix (e.g. a model tag like <c>:120b</c>) must NOT be
    /// stripped — the lookup should fail and return null because the full name is not
    /// in AvailableModels.
    /// </summary>
    [Fact]
    public void TryGetContextWindowForModel_NonReasoningColonSuffix_NotStripped()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "ollama-cloud/gpt-oss", ContextWindow = 128_000 }
                ]
            }
        };

        // The full name with :120b is not in AvailableModels and :120b is not a
        // reasoning level, so it should not be stripped → null.
        Assert.Null(config.TryGetContextWindowForModel("ollama-cloud/gpt-oss:120b"));
    }

    /// <summary>
    /// An unknown model carrying a reasoning suffix should still return null.
    /// </summary>
    [Fact]
    public void TryGetContextWindowForModel_UnknownModelWithSuffix_ReturnsNull()
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

        Assert.Null(config.TryGetContextWindowForModel("copilot/unknown-model:high"));
    }

    // ── TryGetReasoningEffortForModel: reasoning suffix stripping ───────────────

    /// <summary>
    /// A model name with a <c>:high</c> suffix should resolve to the same reasoning
    /// effort as the bare name — the suffix is stripped before matching.
    /// </summary>
    [Fact]
    public void TryGetReasoningEffortForModel_HighSuffix_ReturnsReasoningEffort()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ReasoningEffort = "high" }
                ]
            }
        };

        Assert.Equal("high", config.TryGetReasoningEffortForModel("copilot/claude-sonnet-4.6:high"));
        // Must equal the bare-name lookup
        Assert.Equal(
            config.TryGetReasoningEffortForModel("copilot/claude-sonnet-4.6"),
            config.TryGetReasoningEffortForModel("copilot/claude-sonnet-4.6:high"));
    }

    /// <summary>
    /// Every known reasoning level suffix should be stripped and the correct reasoning
    /// effort returned.
    /// </summary>
    [Theory]
    [InlineData(":none")]
    [InlineData(":low")]
    [InlineData(":medium")]
    [InlineData(":high")]
    [InlineData(":extra_high")]
    public void TryGetReasoningEffortForModel_AllKnownSuffixes_ReturnsReasoningEffort(string suffix)
    {
        const string modelName = "copilot/claude-sonnet-4.6";
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = modelName, ReasoningEffort = "high" }
                ]
            }
        };

        Assert.Equal("high", config.TryGetReasoningEffortForModel(modelName + suffix));
    }

    /// <summary>
    /// No suffix at all — existing behaviour must remain intact (regression check).
    /// </summary>
    [Fact]
    public void TryGetReasoningEffortForModel_NoSuffix_ReturnsReasoningEffort()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ReasoningEffort = "medium" }
                ]
            }
        };

        Assert.Equal("medium", config.TryGetReasoningEffortForModel("copilot/claude-sonnet-4.6"));
    }

    /// <summary>
    /// Case-insensitive suffix stripping — <c>:HIGH</c>, <c>:High</c>, etc. should
    /// all be stripped and resolve correctly.
    /// </summary>
    [Theory]
    [InlineData(":HIGH")]
    [InlineData(":High")]
    public void TryGetReasoningEffortForModel_CaseInsensitiveSuffix_ReturnsReasoningEffort(string suffix)
    {
        const string modelName = "copilot/claude-sonnet-4.6";
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = modelName, ReasoningEffort = "high" }
                ]
            }
        };

        Assert.Equal("high", config.TryGetReasoningEffortForModel(modelName + suffix));
    }

    /// <summary>
    /// A non-reasoning colon suffix (e.g. a model tag like <c>:120b</c>) must NOT be
    /// stripped — the lookup should fail and return null because the full name is not
    /// in AvailableModels.
    /// </summary>
    [Fact]
    public void TryGetReasoningEffortForModel_NonReasoningColonSuffix_NotStripped()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "ollama-cloud/gpt-oss", ReasoningEffort = "high" }
                ]
            }
        };

        // The full name with :120b is not in AvailableModels and :120b is not a
        // reasoning level, so it should not be stripped → null.
        Assert.Null(config.TryGetReasoningEffortForModel("ollama-cloud/gpt-oss:120b"));
    }

    /// <summary>
    /// An unknown model carrying a reasoning suffix should still return null.
    /// </summary>
    [Fact]
    public void TryGetReasoningEffortForModel_UnknownModelWithSuffix_ReturnsNull()
    {
        var config = new HiveConfigFile
        {
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "copilot/claude-sonnet-4.6", ReasoningEffort = "high" }
                ]
            }
        };

        Assert.Null(config.TryGetReasoningEffortForModel("copilot/unknown-model:high"));
    }
}