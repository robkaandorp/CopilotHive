using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

public sealed class ConfigModelServiceTests : IDisposable
{
    private readonly string _tempDir;

    public ConfigModelServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-modeltest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Test 1: Applies OrchestratorModel ────────────────────────────────────

    [Fact]
    public async Task SaveModelConfigAsync_AppliesOrchestratorModel()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig { Model = "old-model" } };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        var update = new ModelConfigUpdate("new-orch", null, null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("new-orch", config.Orchestrator.Model);
    }

    // ── Test 2: Applies ComposerModel, initializes ComposerConfig if null ────

    [Fact]
    public async Task SaveModelConfigAsync_AppliesComposerModel_InitializesIfNull()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        var update = new ModelConfigUpdate(null, "new-composer", null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.NotNull(config.Composer);
        Assert.Equal("new-composer", config.Composer!.Model);
    }

    // ── Test 3: Applies WorkerModels entries ─────────────────────────────────

    [Fact]
    public async Task SaveModelConfigAsync_AppliesWorkerModels()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        var update = new ModelConfigUpdate(null, null, new Dictionary<string, string> { ["coder"] = "special-model" }, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.True(config.Workers.ContainsKey("coder"));
        Assert.Equal("special-model", config.Workers["coder"].Model);
    }

    // ── Test 4: Applies CompactionModel, initializes ModelsConfig if null ────

    [Fact]
    public async Task SaveModelConfigAsync_AppliesCompactionModel_InitializesIfNull()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        var update = new ModelConfigUpdate(null, null, null, null, "compact-model");

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.NotNull(config.Models);
        Assert.Equal("compact-model", config.Models!.CompactionModel);
    }

    // ── Test 5: Calls WriteConfigAsync then CommitFileAsync ─────────────────

    [Fact]
    public async Task SaveModelConfigAsync_CallsWriteThenCommit()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig { Model = "test" } };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        var update = new ModelConfigUpdate("orch", null, null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Single(repo.Commits);
        Assert.Equal("hive-config.yaml", repo.Commits[0].File);
        Assert.StartsWith("chore: update model configuration", repo.Commits[0].Message);
    }

    // ── Test 6: ModelConfigUpdate.Description formats correctly ──────────────

    [Fact]
    public void Description_OrchestratorOnly_ContainsOrchestratorOnly()
    {
        var update = new ModelConfigUpdate("orch", null, null, null, null);
        Assert.Contains("orchestrator→orch", update.Description);
        Assert.DoesNotContain("composer", update.Description);
        Assert.DoesNotContain("compaction", update.Description);
    }

    [Fact]
    public void Description_ComposerAndCompaction_ContainsBoth()
    {
        var update = new ModelConfigUpdate(null, "comp", null, null, "mini");
        Assert.Contains("composer→comp", update.Description);
        Assert.Contains("compaction→mini", update.Description);
    }

    [Fact]
    public void Description_AllFields_ContainsAllSegments()
    {
        var update = new ModelConfigUpdate("orch", "comp", new Dictionary<string, string> { ["reviewer"] = "r-model" }, null, "mini");
        Assert.Contains("orchestrator→orch", update.Description);
        Assert.Contains("composer→comp", update.Description);
        Assert.Contains("compaction→mini", update.Description);
        Assert.Contains("workers:", update.Description);
    }

    // ── Test 7: Description has no trailing commas or empty segments ────────

    [Fact]
    public void Description_AllNull_IsEmptyString()
    {
        var update = new ModelConfigUpdate(null, null, null, null, null);
        Assert.Equal("", update.Description);
    }

    [Fact]
    public async Task SaveModelConfigAsync_AppliesPremiumWorkerModels()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        var update = new ModelConfigUpdate(null, null, null, new Dictionary<string, string> { ["coder"] = "premium-model" }, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.True(config.Workers.ContainsKey("coder"));
        Assert.Equal("premium-model", config.Workers["coder"].PremiumModel);
    }

    [Fact]
    public async Task SaveModelConfigAsync_PremiumWorkerModels_InitializesWorkerConfigIfNull()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        var update = new ModelConfigUpdate(null, null, null, new Dictionary<string, string> { ["tester"] = "tester-premium" }, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.True(config.Workers.ContainsKey("tester"));
        Assert.Equal("tester-premium", config.Workers["tester"].PremiumModel);
    }

    [Fact]
    public void Description_ContainsPremiumWorkers()
    {
        var update = new ModelConfigUpdate(null, null, null, new Dictionary<string, string> { ["reviewer"] = "r-premium" }, null);
        Assert.Contains("premium:", update.Description);
        Assert.Contains("reviewer→r-premium", update.Description);
    }

    [Fact]
    public void Description_SingleField_NoTrailingCommasOrDoubleCommas()
    {
        var update = new ModelConfigUpdate("only-orch", null, null, null, null);
        var desc = update.Description;
        Assert.DoesNotMatch("^,", desc);
        Assert.DoesNotMatch(",$", desc);
        Assert.DoesNotContain(", ,", desc);
    }

    // ── UpdateModelAsync Wiring Tests ──────────────────────────────────────────

    [Fact]
    public async Task SaveModelConfigAsync_WithOrchestratorModel_CallsBrainUpdateModelAsync()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig { Model = "old-model" } };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);
        var update = new ModelConfigUpdate("new-orch", null, null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("new-orch", brain.LastModel);
    }

    [Fact]
    public async Task SaveModelConfigAsync_ModelInAvailableModels_PassesContextWindow()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "old-model" },
            Models = new ModelsConfig
            {
                AvailableModels = new List<ModelEntry>
                {
                    new() { Name = "new-orch", ContextWindow = 256000 }
                }
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);
        var update = new ModelConfigUpdate("new-orch", null, null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("new-orch", brain.LastModel);
        Assert.Equal(256000, brain.LastMaxContextTokens);
    }

    [Fact]
    public async Task SaveModelConfigAsync_ModelWithReasoningEffort_AppliesSuffixToBrain()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "old-model" },
            Models = new ModelsConfig
            {
                AvailableModels = new List<ModelEntry>
                {
                    new() { Name = "new-orch", ContextWindow = 256000, ReasoningEffort = "high" }
                }
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);
        var update = new ModelConfigUpdate("new-orch", null, null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("new-orch:high", brain.LastModel);
        Assert.Equal(256000, brain.LastMaxContextTokens);
    }

    [Fact]
    public async Task SaveModelConfigAsync_ModelWithoutReasoningEffort_NoSuffixApplied()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "old-model" },
            Models = new ModelsConfig
            {
                AvailableModels = new List<ModelEntry>
                {
                    new() { Name = "new-orch", ContextWindow = 256000 }
                }
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);
        var update = new ModelConfigUpdate("new-orch", null, null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("new-orch", brain.LastModel);
    }

    [Fact]
    public async Task SaveModelConfigAsync_ModelNotInAvailableModels_FallsBackToDefaultBrainContextWindow()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "old-model" },
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);
        var update = new ModelConfigUpdate("unknown-model", null, null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("unknown-model", brain.LastModel);
        Assert.Equal(Constants.DefaultBrainContextWindow, brain.LastMaxContextTokens);
    }

    [Fact]
    public async Task SaveModelConfigAsync_NeitherLookupYieldsValue_PassesDefaultBrainContextWindow()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "old-model" },
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);
        var update = new ModelConfigUpdate("unknown-model", null, null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("unknown-model", brain.LastModel);
        Assert.Equal(Constants.DefaultBrainContextWindow, brain.LastMaxContextTokens);
    }

    [Fact]
    public async Task SaveModelConfigAsync_NullOrchestratorModel_DoesNotCallUpdateModelAsync()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig { Model = "old-model" } };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);
        // OrchestratorModel is null — only ComposerModel is set
        var update = new ModelConfigUpdate(null, "new-composer", null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Null(brain.LastModel);
        Assert.Null(brain.LastMaxContextTokens);
    }

    // ── AddAvailableModelAsync tests ─────────────────────────────────────────

    [Fact]
    public async Task AddAvailableModelAsync_AddsModelToConfig()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("copilot/claude-sonnet-4.6", 200000, "high", TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.AvailableModels!);
        Assert.Equal("copilot/claude-sonnet-4.6", model.Name);
        Assert.Equal(200000, model.ContextWindow);
        Assert.Equal("high", model.ReasoningEffort);
    }

    // ── AddAvailableModelAsync suffix-stripping tests ─────────────────────────

    [Fact]
    public async Task AddAvailableModelAsync_StripsKnownSuffix_FromName()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("copilot/claude-sonnet-4.6:high", null, null, TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.AvailableModels!);
        Assert.Equal("copilot/claude-sonnet-4.6", model.Name);
        Assert.Equal("high", model.ReasoningEffort);
    }

    [Fact]
    public async Task AddAvailableModelAsync_UnknownSuffix_LeavesNameUntouched()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("model:custom", null, null, TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.AvailableModels!);
        Assert.Equal("model:custom", model.Name);
        Assert.Null(model.ReasoningEffort);
    }

    [Fact]
    public async Task AddAvailableModelAsync_ExplicitReasoningEffort_TakesPrecedenceOverSuffix()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("model:high", null, "low", TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.AvailableModels!);
        Assert.Equal("model", model.Name);
        Assert.Equal("low", model.ReasoningEffort);
    }

    [Fact]
    public async Task AddAvailableModelAsync_NoSuffix_NoReasoningEffort()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("plain-model", 100000, null, TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.AvailableModels!);
        Assert.Equal("plain-model", model.Name);
        Assert.Null(model.ReasoningEffort);
        Assert.Equal(100000, model.ContextWindow);
    }

    [Fact]
    public async Task AddAvailableModelAsync_InitializesModelsConfigIfNull()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("model-a", null, null, TestContext.Current.CancellationToken);

        Assert.NotNull(config.Models);
        var model = Assert.Single(config.Models!.AvailableModels!);
        Assert.Equal("model-a", model.Name);
    }

    [Fact]
    public async Task AddAvailableModelAsync_DuplicateThrows()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("model-a", null, null, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AddAvailableModelAsync("MODEL-A", null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddAvailableModelAsync_WritesAndCommits()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("model-a", null, null, TestContext.Current.CancellationToken);

        Assert.Single(repo.Commits);
        Assert.Equal("hive-config.yaml", repo.Commits[0].File);
        Assert.Contains("add available model", repo.Commits[0].Message);
    }

    // ── UpdateAvailableModelAsync tests ──────────────────────────────────────

    [Fact]
    public async Task UpdateAvailableModelAsync_UpdatesContextWindow()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "model-a", ContextWindow = 128000 }]
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateAvailableModelAsync("model-a", 256000, null, TestContext.Current.CancellationToken);

        Assert.Equal(256000, config.Models!.AvailableModels![0].ContextWindow);
    }

    [Fact]
    public async Task UpdateAvailableModelAsync_UpdatesReasoningEffort()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "model-a", ReasoningEffort = null }]
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateAvailableModelAsync("model-a", null, "high", TestContext.Current.CancellationToken);

        Assert.Equal("high", config.Models!.AvailableModels![0].ReasoningEffort);
    }

    [Fact]
    public async Task UpdateAvailableModelAsync_ClearsReasoningEffort_WhenPassedNull()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "model-a", ReasoningEffort = "high" }]
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateAvailableModelAsync("model-a", null, null, TestContext.Current.CancellationToken);

        Assert.Null(config.Models!.AvailableModels![0].ReasoningEffort);
    }

    [Fact]
    public async Task UpdateAvailableModelAsync_NotFoundThrows()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateAvailableModelAsync("missing", 1000, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateAvailableModelAsync_WritesAndCommits()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "model-a", ContextWindow = 128000 }]
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateAvailableModelAsync("model-a", 256000, null, TestContext.Current.CancellationToken);

        Assert.Single(repo.Commits);
        Assert.Equal("hive-config.yaml", repo.Commits[0].File);
        Assert.Contains("update available model", repo.Commits[0].Message);
    }

    // ── RemoveAvailableModelAsync tests ──────────────────────────────────────

    [Fact]
    public async Task RemoveAvailableModelAsync_RemovesModel()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "model-a" },
                    new ModelEntry { Name = "model-b" }
                ]
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.RemoveAvailableModelAsync("model-a", TestContext.Current.CancellationToken);

        var remaining = Assert.Single(config.Models!.AvailableModels!);
        Assert.Equal("model-b", remaining.Name);
    }

    [Fact]
    public async Task RemoveAvailableModelAsync_NotFoundReturnsFalse()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var result = await svc.RemoveAvailableModelAsync("missing", TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task RemoveAvailableModelAsync_RemovedModel_ReturnsTrue()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "model-a" }]
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var result = await svc.RemoveAvailableModelAsync("model-a", TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task RemoveAvailableModelAsync_WritesAndCommits()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "model-a" }]
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.RemoveAvailableModelAsync("model-a", TestContext.Current.CancellationToken);

        Assert.Single(repo.Commits);
        Assert.Equal("hive-config.yaml", repo.Commits[0].File);
        Assert.Contains("remove available model", repo.Commits[0].Message);
    }

    [Fact]
    public async Task RemoveAvailableModelAsync_CaseInsensitive()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "copilot/claude-sonnet-4.6" }]
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var result = await svc.RemoveAvailableModelAsync("COPILOT/CLAUDE-SONNET-4.6", TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Empty(config.Models!.AvailableModels!);
    }

    // ── AddRepositoryAsync tests ─────────────────────────────────────────────

    [Fact]
    public async Task AddRepositoryAsync_AddsRepositoryToConfig()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main", TestContext.Current.CancellationToken);

        var added = Assert.Single(config.Repositories);
        Assert.Equal("my-repo", added.Name);
        Assert.Equal("https://github.com/org/repo.git", added.Url);
        Assert.Equal("main", added.DefaultBranch);
    }

    [Fact]
    public async Task AddRepositoryAsync_DuplicateThrows()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AddRepositoryAsync("MY-REPO", "https://github.com/org/other.git", "develop", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddRepositoryAsync_DefaultsBranchToMain()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "", TestContext.Current.CancellationToken);

        var added = Assert.Single(config.Repositories);
        Assert.Equal("main", added.DefaultBranch);
    }

    [Fact]
    public async Task AddRepositoryAsync_WritesAndCommits()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main", TestContext.Current.CancellationToken);

        Assert.Single(repo.Commits);
        Assert.Equal("hive-config.yaml", repo.Commits[0].File);
        Assert.Contains("add repository", repo.Commits[0].Message);
    }

    // ── UpdateRepositoryAsync tests ──────────────────────────────────────────

    [Fact]
    public async Task UpdateRepositoryAsync_UpdatesUrlAndBranch()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories = [new RepositoryConfig { Name = "my-repo", Url = "https://github.com/org/old.git", DefaultBranch = "main" }]
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateRepositoryAsync("my-repo", "https://github.com/org/new.git", "develop", TestContext.Current.CancellationToken);

        var updated = Assert.Single(config.Repositories);
        Assert.Equal("https://github.com/org/new.git", updated.Url);
        Assert.Equal("develop", updated.DefaultBranch);
    }

    [Fact]
    public async Task UpdateRepositoryAsync_NotFoundThrows()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateRepositoryAsync("missing", "https://github.com/org/new.git", "main", TestContext.Current.CancellationToken));
    }

    // ── RemoveRepositoryAsync tests ──────────────────────────────────────────

    [Fact]
    public async Task RemoveRepositoryAsync_RemovesRepository()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig { Name = "repo-a", Url = "https://github.com/org/a.git", DefaultBranch = "main" },
                new RepositoryConfig { Name = "repo-b", Url = "https://github.com/org/b.git", DefaultBranch = "main" }
            ]
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.RemoveRepositoryAsync("repo-a", TestContext.Current.CancellationToken);

        var remaining = Assert.Single(config.Repositories);
        Assert.Equal("repo-b", remaining.Name);
    }

    [Fact]
    public async Task RemoveRepositoryAsync_NotFoundReturnsFalse()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var result = await svc.RemoveRepositoryAsync("missing", TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task RemoveRepositoryAsync_WritesAndCommits()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories = [new RepositoryConfig { Name = "repo-a", Url = "https://github.com/org/a.git", DefaultBranch = "main" }]
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.RemoveRepositoryAsync("repo-a", TestContext.Current.CancellationToken);

        Assert.Single(repo.Commits);
        Assert.Equal("hive-config.yaml", repo.Commits[0].File);
        Assert.Contains("remove repository", repo.Commits[0].Message);
    }

    // ── UpdateOrchestratorSettingsAsync tests ────────────────────────────────

    [Fact]
    public async Task UpdateOrchestratorSettingsAsync_UpdatesAllFields()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        var update = new OrchestratorSettingsUpdate(
            MaxIterations: 99, MaxRetriesPerTask: 7, MaxParallelGoals: 4,
            AlwaysImprove: true, VerboseLogging: true,
            BrainMaxSteps: 120,
            BranchCleanupDelayHours: 12);

        await svc.UpdateOrchestratorSettingsAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal(99, config.Orchestrator.MaxIterations);
        Assert.Equal(7, config.Orchestrator.MaxRetriesPerTask);
        Assert.Equal(4, config.Orchestrator.MaxParallelGoals);
        Assert.True(config.Orchestrator.AlwaysImprove);
        Assert.True(config.Orchestrator.VerboseLogging);
        Assert.Equal(120, config.Orchestrator.BrainMaxSteps);
        Assert.Equal(12, config.Orchestrator.BranchCleanupDelayHours);
    }

    [Fact]
    public async Task UpdateOrchestratorSettingsAsync_PartialUpdate_OnlyChangesProvidedFields()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig
            {
                MaxIterations = 10,
                MaxRetriesPerTask = 3,
                AlwaysImprove = false,
                VerboseLogging = false
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        var update = new OrchestratorSettingsUpdate(
            MaxIterations: 50, MaxRetriesPerTask: null, MaxParallelGoals: null,
            AlwaysImprove: true, VerboseLogging: null,
            BrainMaxSteps: null,
            BranchCleanupDelayHours: null);

        await svc.UpdateOrchestratorSettingsAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal(50, config.Orchestrator.MaxIterations);
        Assert.True(config.Orchestrator.AlwaysImprove);
        // Unchanged
        Assert.Equal(3, config.Orchestrator.MaxRetriesPerTask);
        Assert.False(config.Orchestrator.VerboseLogging);
    }

    [Fact]
    public async Task UpdateOrchestratorSettingsAsync_WritesAndCommits()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        var update = new OrchestratorSettingsUpdate(
            MaxIterations: 5, MaxRetriesPerTask: null, MaxParallelGoals: null,
            AlwaysImprove: null, VerboseLogging: null,
            BrainMaxSteps: null,
            BranchCleanupDelayHours: null);

        await svc.UpdateOrchestratorSettingsAsync(update, TestContext.Current.CancellationToken);

        Assert.Single(repo.Commits);
        Assert.Equal("hive-config.yaml", repo.Commits[0].File);
        Assert.Contains("orchestrator", repo.Commits[0].Message);
    }

    // ── UpdateWorkerContextWindowsAsync tests ────────────────────────────────

    [Fact]
    public async Task UpdateWorkerContextWindowsAsync_UpdatesContextWindows()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Workers = new Dictionary<string, WorkerConfig>
            {
                ["coder"] = new WorkerConfig(),
                ["tester"] = new WorkerConfig()
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateWorkerContextWindowsAsync(
            new Dictionary<string, int> { ["coder"] = 50000, ["tester"] = 30000 },
            TestContext.Current.CancellationToken);

        Assert.Equal(50000, config.Workers["coder"].ContextWindow);
        Assert.Equal(30000, config.Workers["tester"].ContextWindow);
    }

    [Fact]
    public async Task UpdateWorkerContextWindowsAsync_CreatesWorkerIfMissing()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateWorkerContextWindowsAsync(
            new Dictionary<string, int> { ["reviewer"] = 40000 },
            TestContext.Current.CancellationToken);

        Assert.True(config.Workers.ContainsKey("reviewer"));
        Assert.Equal(40000, config.Workers["reviewer"].ContextWindow);
    }

    [Fact]
    public async Task UpdateWorkerContextWindowsAsync_WritesAndCommits()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateWorkerContextWindowsAsync(
            new Dictionary<string, int> { ["coder"] = 50000 },
            TestContext.Current.CancellationToken);

        Assert.Single(repo.Commits);
        Assert.Equal("hive-config.yaml", repo.Commits[0].File);
        Assert.Contains("worker", repo.Commits[0].Message);
    }

    // ── UpdateComposerSettingsAsync tests ────────────────────────────────────

    [Fact]
    public async Task UpdateComposerSettingsAsync_UpdatesContextWindow()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { MaxSteps = 50 }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(50, config.Composer!.MaxSteps);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_UpdatesMaxSteps()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { MaxSteps = 50 }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(99, TestContext.Current.CancellationToken);

        Assert.Equal(99, config.Composer!.MaxSteps);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_InitializesComposerIfNull()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Composer = null };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(50, TestContext.Current.CancellationToken);

        Assert.NotNull(config.Composer);
        Assert.Equal(50, config.Composer!.MaxSteps);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_WritesAndCommits()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(50, TestContext.Current.CancellationToken);

        Assert.Single(repo.Commits);
        Assert.Equal("hive-config.yaml", repo.Commits[0].File);
        Assert.Contains("composer", repo.Commits[0].Message);
    }

    // ── YAML write-back tests ──────────────────────────────────────────────────

    [Fact]
    public async Task AddRepositoryAsync_WritesYamlWithRepository()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main", TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("my-repo", yaml);
        Assert.Contains("https://github.com/org/repo.git", yaml);
        Assert.Contains("main", yaml);
    }

    [Fact]
    public async Task UpdateOrchestratorSettingsAsync_WritesYamlWithSettings()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        var update = new OrchestratorSettingsUpdate(
            MaxIterations: 15, MaxRetriesPerTask: 5, MaxParallelGoals: 3,
            AlwaysImprove: true, VerboseLogging: true,
            BrainMaxSteps: 75,
            BranchCleanupDelayHours: 24);

        await svc.UpdateOrchestratorSettingsAsync(update, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("max_iterations: 15", yaml);
        Assert.Contains("max_retries_per_task: 5", yaml);
        Assert.Contains("max_parallel_goals: 3", yaml);
        Assert.Contains("always_improve: true", yaml);
        Assert.Contains("verbose_logging: true", yaml);
        Assert.Contains("brain_max_steps: 75", yaml);
        Assert.Contains("branch_cleanup_delay_hours: 24", yaml);
    }

    [Fact]
    public async Task UpdateWorkerContextWindowsAsync_WritesYamlWithContextWindows()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateWorkerContextWindowsAsync(
            new Dictionary<string, int> { ["coder"] = 50000, ["tester"] = 30000 },
            TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("context_window: 50000", yaml);
        Assert.Contains("context_window: 30000", yaml);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_WritesYamlWithComposerSettings()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(75, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("max_steps: 75", yaml);
    }

    // ── Clone-triggering tests ─────────────────────────────────────────────────

    [Fact]
    public async Task AddRepositoryAsync_CallsEnsureCloneAsync()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var repoManager = new FakeBrainRepoManager();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, null, repoManager);

        await svc.AddRepositoryAsync("test-repo", "https://github.com/org/repo.git", "main", TestContext.Current.CancellationToken);

        var call = Assert.Single(repoManager.CloneCalls);
        Assert.Equal("test-repo", call.Name);
        Assert.Equal("https://github.com/org/repo.git", call.Url);
        Assert.Equal("main", call.Branch);
    }

    [Fact]
    public async Task UpdateRepositoryAsync_CallsEnsureCloneAsync()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories = [new RepositoryConfig { Name = "existing-repo", Url = "https://old.com/repo.git", DefaultBranch = "main" }]
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var repoManager = new FakeBrainRepoManager();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, null, repoManager);

        await svc.UpdateRepositoryAsync("existing-repo", "https://new.com/repo.git", "develop", TestContext.Current.CancellationToken);

        var call = Assert.Single(repoManager.CloneCalls);
        Assert.Equal("existing-repo", call.Name);
        Assert.Equal("https://new.com/repo.git", call.Url);
        Assert.Equal("develop", call.Branch);
    }

    // ── Validation tests ───────────────────────────────────────────────────────

    [Fact]
    public async Task AddRepositoryAsync_RejectsPathTraversalName()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddRepositoryAsync("../../etc", "https://github.com/org/repo.git", "main", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddRepositoryAsync_RejectsNullUrl()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddRepositoryAsync("test-repo", "", "main", TestContext.Current.CancellationToken));
    }
}

/// <summary>
/// Minimal fake implementing <see cref="IDistributedBrain"/> for unit tests.
/// </summary>
file sealed class FakeDistributedBrain : IDistributedBrain
{
    public bool Connected { get; private set; }
    public int PlanIterationCalls { get; private set; }
    public int CraftCalls { get; private set; }
    public string? LastModel { get; private set; }
    public int? LastMaxContextTokens { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default) { Connected = true; return Task.CompletedTask; }

    public Task UpdateModelAsync(string model, int? maxContextTokens = null, CancellationToken ct = default)
    {
        LastModel = model;
        LastMaxContextTokens = maxContextTokens;
        return Task.CompletedTask;
    }

    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
    {
        PlanIterationCalls++;
        return Task.FromResult(PlanResult.Success(IterationPlan.Default()));
    }

    public Task<PromptResult> CraftPromptAsync(
        GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
    {
        CraftCalls++;
        return Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));
    }

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) => Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) => Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) => Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(
        string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default) =>
        Task.FromResult(BrainResponse.Answer("Brain is not available. Please proceed with your best judgment."));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public void DeleteGoalSession(string goalId) { }

    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

    public BrainStats? GetStats() => null;
}

/// <summary>
/// Minimal fake implementing <see cref="IBrainRepoManager"/> that records clone calls.
/// </summary>
file sealed class FakeBrainRepoManager : IBrainRepoManager
{
    public string WorkDirectory => "/fake/work";
    public List<(string Name, string Url, string Branch)> CloneCalls { get; } = [];

    public Task<string> EnsureCloneAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default)
    {
        CloneCalls.Add((repoName, repoUrl, defaultBranch));
        return Task.FromResult($"/fake/work/{repoName}");
    }

    public Task<string> MergeFeatureBranchAsync(string repoName, string featureBranch, string defaultBranch, string commitMessage, CancellationToken ct = default) =>
        Task.FromResult("fake-sha");
    public Task<BranchDeleteResult> DeleteRemoteBranchAsync(string repoName, string branchName, CancellationToken ct = default) =>
        Task.FromResult(BranchDeleteResult.Success);
    public string GetClonePath(string repoName) => $"/fake/work/{repoName}";
    public Task<string?> GetHeadShaAsync(string repoName, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
}
