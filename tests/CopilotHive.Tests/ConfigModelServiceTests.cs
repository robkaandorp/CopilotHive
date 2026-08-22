using System.Net;
using System.Net.Http.Json;
using CopilotHive.Configuration;
using CopilotHive.Git;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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
    public async Task SaveModelConfigAsync_ModelWithReasoningEffort_SendsPlainModelNameToBrain()
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

        // The legacy model-name suffix mechanism is gone — the plain model name is sent.
        Assert.Equal("new-orch", brain.LastModel);
        Assert.Equal(256000, brain.LastMaxContextTokens);
    }

    [Fact]
    public async Task SaveModelConfigAsync_PassesOrchestratorReasoningEffortEnum_ToBrain()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "old-model", ReasoningEffort = "high" },
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);
        var update = new ModelConfigUpdate("new-orch", null, null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal(1, brain.ReasoningCaptureCalls);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.High, brain.LastReasoningEffort);
        // The model string carries no reasoning suffix — reasoning travels as a separate argument.
        Assert.Equal("new-orch", brain.LastModel);
    }

    [Fact]
    public async Task SaveModelConfigAsync_NoOrchestratorReasoningEffort_PassesNullEnum_ToBrain()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "old-model" },
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);
        var update = new ModelConfigUpdate("new-orch", null, null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal(1, brain.ReasoningCaptureCalls);
        Assert.Null(brain.LastReasoningEffort);
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

        await svc.AddAvailableModelAsync("copilot/claude-sonnet-4.6", 200000, ct: TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.AvailableModels!);
        Assert.Equal("copilot/claude-sonnet-4.6", model.Name);
        Assert.Equal(200000, model.ContextWindow);
        // Available models no longer store a reasoning effort — the parameter is ignored.
        Assert.Null(model.ReasoningEffort);
    }

    // ── AddAvailableModelAsync plain-name tests ───────────────────────────────

    /// <summary>
    /// Model names are stored verbatim — a trailing <c>:high</c> is no longer stripped,
    /// and no reasoning effort is ever persisted on an available model.
    /// </summary>
    [Fact]
    public async Task AddAvailableModelAsync_LegacySuffix_StoredVerbatim_WithoutReasoningEffort()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("copilot/claude-sonnet-4.6:high", null, ct: TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.AvailableModels!);
        Assert.Equal("copilot/claude-sonnet-4.6:high", model.Name);
        Assert.Null(model.ReasoningEffort);
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

        await svc.AddAvailableModelAsync("model:custom", null, ct: TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.AvailableModels!);
        Assert.Equal("model:custom", model.Name);
        Assert.Null(model.ReasoningEffort);
    }

    [Fact]
    public async Task AddAvailableModelAsync_NeverPersistsReasoningEffort()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("model:high", null, ct: TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.AvailableModels!);
        Assert.Equal("model:high", model.Name);
        // Available models carry no reasoning effort at all — there is no parameter to supply one.
        Assert.Null(model.ReasoningEffort);
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

        await svc.AddAvailableModelAsync("plain-model", 100000, ct: TestContext.Current.CancellationToken);

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

        await svc.AddAvailableModelAsync("model-a", null, ct: TestContext.Current.CancellationToken);

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

        await svc.AddAvailableModelAsync("model-a", null, ct: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AddAvailableModelAsync("MODEL-A", null, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddAvailableModelAsync_WritesAndCommits()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("model-a", null, ct: TestContext.Current.CancellationToken);

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

        await svc.UpdateAvailableModelAsync("model-a", 256000, ct: TestContext.Current.CancellationToken);

        Assert.Equal(256000, config.Models!.AvailableModels![0].ContextWindow);
    }

    [Fact]
    public async Task UpdateAvailableModelAsync_PreservesExistingReasoningEffort()
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

        await svc.UpdateAvailableModelAsync("model-a", null, ct: TestContext.Current.CancellationToken);

        // The PUT has no reasoning parameter — an existing value is neither set nor cleared.
        Assert.Equal("high", config.Models!.AvailableModels![0].ReasoningEffort);
    }

    [Fact]
    public async Task UpdateAvailableModelAsync_NotFoundThrows()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateAvailableModelAsync("missing", 1000, ct: TestContext.Current.CancellationToken));
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

        await svc.UpdateAvailableModelAsync("model-a", 256000, ct: TestContext.Current.CancellationToken);

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

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main", release: null, ct: TestContext.Current.CancellationToken);

        var added = Assert.Single(config.Repositories);
        Assert.Equal("my-repo", added.Name);
        Assert.Equal("https://github.com/org/repo.git", added.Url);
        Assert.Equal("main", added.DefaultBranch);
        Assert.Null(added.Release);
    }

    [Fact]
    public async Task AddRepositoryAsync_WithRelease_StoresRelease()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main",
            new ReleaseRepoConfig { MergeTo = "main", TagBranch = "main" }, ct: TestContext.Current.CancellationToken);

        var added = Assert.Single(config.Repositories);
        Assert.NotNull(added.Release);
        Assert.Equal("main", added.Release!.MergeTo);
        Assert.Equal("main", added.Release!.TagBranch);
    }

    [Fact]
    public async Task AddRepositoryAsync_WithEmptyRelease_NormalizesToNull()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main",
            new ReleaseRepoConfig(), ct: TestContext.Current.CancellationToken);

        var added = Assert.Single(config.Repositories);
        Assert.Null(added.Release);
    }

    [Fact]
    public async Task AddRepositoryAsync_WithOnlyMergeTo_PreservesRelease()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main",
            new ReleaseRepoConfig { MergeTo = "main", TagBranch = null }, ct: TestContext.Current.CancellationToken);

        var added = Assert.Single(config.Repositories);
        Assert.NotNull(added.Release);
        Assert.Equal("main", added.Release!.MergeTo);
        Assert.Null(added.Release!.TagBranch);
    }

    [Fact]
    public async Task AddRepositoryAsync_DuplicateThrows()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main", release: null, ct: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AddRepositoryAsync("MY-REPO", "https://github.com/org/other.git", "develop", release: null, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddRepositoryAsync_DefaultsBranchToMain()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "", release: null, ct: TestContext.Current.CancellationToken);

        var added = Assert.Single(config.Repositories);
        Assert.Equal("main", added.DefaultBranch);
    }

    [Fact]
    public async Task AddRepositoryAsync_WritesAndCommits()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main", release: null, ct: TestContext.Current.CancellationToken);

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

        await svc.UpdateRepositoryAsync("my-repo", "https://github.com/org/new.git", "develop", release: null, ct: TestContext.Current.CancellationToken);

        var updated = Assert.Single(config.Repositories);
        Assert.Equal("https://github.com/org/new.git", updated.Url);
        Assert.Equal("develop", updated.DefaultBranch);
    }

    [Fact]
    public async Task UpdateRepositoryAsync_WithRelease_UpdatesRelease()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-repo",
                    Url = "https://github.com/org/old.git",
                    DefaultBranch = "main",
                    Release = new ReleaseRepoConfig { MergeTo = "develop", TagBranch = "develop" }
                }
            ]
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateRepositoryAsync("my-repo", "https://github.com/org/new.git", "main",
            new ReleaseRepoConfig { MergeTo = "main", TagBranch = "main" }, ct: TestContext.Current.CancellationToken);

        var updated = Assert.Single(config.Repositories);
        Assert.Equal("main", updated.Release!.MergeTo);
        Assert.Equal("main", updated.Release!.TagBranch);
    }

    [Fact]
    public async Task UpdateRepositoryAsync_NullRelease_PreservesExistingRelease()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-repo",
                    Url = "https://github.com/org/old.git",
                    DefaultBranch = "main",
                    Release = new ReleaseRepoConfig { MergeTo = "develop", TagBranch = "develop" }
                }
            ]
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateRepositoryAsync("my-repo", "https://github.com/org/new.git", "main", release: null, ct: TestContext.Current.CancellationToken);

        var updated = Assert.Single(config.Repositories);
        Assert.Equal("develop", updated.Release!.MergeTo);
        Assert.Equal("develop", updated.Release!.TagBranch);
    }

    [Fact]
    public async Task UpdateRepositoryAsync_EmptyRelease_NormalizesToNull()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-repo",
                    Url = "https://github.com/org/old.git",
                    DefaultBranch = "main",
                    Release = new ReleaseRepoConfig { MergeTo = "develop", TagBranch = "develop" }
                }
            ]
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateRepositoryAsync("my-repo", "https://github.com/org/new.git", "main",
            new ReleaseRepoConfig(), ct: TestContext.Current.CancellationToken);

        var updated = Assert.Single(config.Repositories);
        Assert.Null(updated.Release);
    }

    [Fact]
    public async Task UpdateRepositoryAsync_NotFoundThrows()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateRepositoryAsync("missing", "https://github.com/org/new.git", "main", release: null, ct: TestContext.Current.CancellationToken));
    }

    // ── CI monitoring fields ─────────────────────────────────────────────────

    [Fact]
    public async Task AddRepositoryAsync_WithMonitorCiAndTimeout_SetsBoth()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main",
            monitorCi: true, ciTimeoutMinutes: 45, ct: TestContext.Current.CancellationToken);

        var added = Assert.Single(config.Repositories);
        Assert.True(added.MonitorCi);
        Assert.Equal(45, added.CiTimeoutMinutes);
    }

    [Fact]
    public async Task AddRepositoryAsync_NullMonitorCiAndTimeout_RetainsDefaults()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main",
            release: null, monitorCi: null, ciTimeoutMinutes: null, ct: TestContext.Current.CancellationToken);

        var added = Assert.Single(config.Repositories);
        Assert.False(added.MonitorCi);
        Assert.Equal(30, added.CiTimeoutMinutes);
    }

    [Fact]
    public async Task AddRepositoryAsync_ZeroCiTimeout_Throws()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main",
                ciTimeoutMinutes: 0, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddRepositoryAsync_CiTimeoutAboveRange_Throws()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Repositories = [] };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main",
                ciTimeoutMinutes: 121, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateRepositoryAsync_WithMonitorCi_SetsItAndPreservesTimeout()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-repo",
                    Url = "https://github.com/org/old.git",
                    DefaultBranch = "main",
                    CiTimeoutMinutes = 45
                }
            ]
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateRepositoryAsync("my-repo", "https://github.com/org/new.git", "main",
            monitorCi: true, ct: TestContext.Current.CancellationToken);

        var updated = Assert.Single(config.Repositories);
        Assert.True(updated.MonitorCi);
        Assert.Equal(45, updated.CiTimeoutMinutes);
    }

    [Fact]
    public async Task UpdateRepositoryAsync_NullMonitorCiAndTimeout_PreservesExistingValues()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Repositories =
            [
                new RepositoryConfig
                {
                    Name = "my-repo",
                    Url = "https://github.com/org/old.git",
                    DefaultBranch = "main",
                    MonitorCi = true,
                    CiTimeoutMinutes = 60
                }
            ]
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateRepositoryAsync("my-repo", "https://github.com/org/new.git", "main",
            release: null, monitorCi: null, ciTimeoutMinutes: null, ct: TestContext.Current.CancellationToken);

        var updated = Assert.Single(config.Repositories);
        Assert.True(updated.MonitorCi);
        Assert.Equal(60, updated.CiTimeoutMinutes);
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
            VerboseLogging: true,
            BrainMaxSteps: 120,
            BranchCleanupDelayHours: 12);

        await svc.UpdateOrchestratorSettingsAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal(99, config.Orchestrator.MaxIterations);
        Assert.Equal(7, config.Orchestrator.MaxRetriesPerTask);
        Assert.Equal(4, config.Orchestrator.MaxParallelGoals);
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
                VerboseLogging = false
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);
        var update = new OrchestratorSettingsUpdate(
            MaxIterations: 50, MaxRetriesPerTask: null, MaxParallelGoals: null,
            VerboseLogging: null,
            BrainMaxSteps: null,
            BranchCleanupDelayHours: null);

        await svc.UpdateOrchestratorSettingsAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal(50, config.Orchestrator.MaxIterations);
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
            VerboseLogging: null,
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

        await svc.UpdateComposerSettingsAsync(new ComposerSettingsUpdate(), TestContext.Current.CancellationToken);

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

        await svc.UpdateComposerSettingsAsync(new ComposerSettingsUpdate(MaxSteps: 99), TestContext.Current.CancellationToken);

        Assert.Equal(99, config.Composer!.MaxSteps);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_InitializesComposerIfNull()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig(), Composer = null };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(new ComposerSettingsUpdate(MaxSteps: 50), TestContext.Current.CancellationToken);

        Assert.NotNull(config.Composer);
        Assert.Equal(50, config.Composer!.MaxSteps);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_WritesAndCommits()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(new ComposerSettingsUpdate(MaxSteps: 50), TestContext.Current.CancellationToken);

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

        await svc.AddRepositoryAsync("my-repo", "https://github.com/org/repo.git", "main", release: null, ct: TestContext.Current.CancellationToken);

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
            VerboseLogging: true,
            BrainMaxSteps: 75,
            BranchCleanupDelayHours: 24);

        await svc.UpdateOrchestratorSettingsAsync(update, TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("max_iterations: 15", yaml);
        Assert.Contains("max_retries_per_task: 5", yaml);
        Assert.Contains("max_parallel_goals: 3", yaml);
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

        await svc.UpdateComposerSettingsAsync(new ComposerSettingsUpdate(MaxSteps: 75), TestContext.Current.CancellationToken);

        var yaml = await File.ReadAllTextAsync(Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);
        Assert.Contains("max_steps: 75", yaml);
    }

    // ── UpdateComposerSettingsAsync — event notifications ────────────────────

    [Fact]
    public async Task UpdateComposerSettingsAsync_ValidModeEventsThrottle_Persisted()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(
            new ComposerSettingsUpdate(
                EventNotificationsMode: "active",
                EventNotificationsActiveEvents: ["goal_completed", "ci_failed"],
                EventNotificationsThrottleSeconds: 60),
            TestContext.Current.CancellationToken);

        Assert.NotNull(config.Composer);
        Assert.NotNull(config.Composer!.EventNotifications);
        Assert.Equal("active", config.Composer.EventNotifications!.Mode);
        Assert.Equal(["goal_completed", "ci_failed"], config.Composer.EventNotifications.ActiveEvents);
        Assert.Equal(60, config.Composer.EventNotifications.ThrottleSeconds);
        Assert.Single(repo.Commits);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_InvalidMode_ThrowsAndDoesNotMutate()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { MaxSteps = 50 }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.UpdateComposerSettingsAsync(
                new ComposerSettingsUpdate(EventNotificationsMode: "bogus"),
                TestContext.Current.CancellationToken));

        Assert.Null(config.Composer!.EventNotifications);
        Assert.Equal(50, config.Composer.MaxSteps);
        Assert.Empty(repo.Commits);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_InvalidEvent_ThrowsAndDoesNotMutate()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { MaxSteps = 50 }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.UpdateComposerSettingsAsync(
                new ComposerSettingsUpdate(EventNotificationsActiveEvents: ["not_an_event"]),
                TestContext.Current.CancellationToken));

        Assert.Null(config.Composer!.EventNotifications);
        Assert.Equal(50, config.Composer.MaxSteps);
        Assert.Empty(repo.Commits);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_EmptyActiveEvents_Throws()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { MaxSteps = 50 }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.UpdateComposerSettingsAsync(
                new ComposerSettingsUpdate(EventNotificationsActiveEvents: []),
                TestContext.Current.CancellationToken));

        Assert.Null(config.Composer!.EventNotifications);
        Assert.Empty(repo.Commits);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_NullActiveEventEntry_Throws()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.UpdateComposerSettingsAsync(
                new ComposerSettingsUpdate(EventNotificationsActiveEvents: ["goal_completed", null!]),
                TestContext.Current.CancellationToken));

        Assert.Null(config.Composer);
        Assert.Empty(repo.Commits);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_NullFields_NoChange()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig
            {
                MaxSteps = 50,
                EventNotifications = new EventNotificationsConfig
                {
                    Mode = "active",
                    ActiveEvents = ["goal_completed"],
                    ThrottleSeconds = 45
                }
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(new ComposerSettingsUpdate(), TestContext.Current.CancellationToken);

        Assert.Equal(50, config.Composer!.MaxSteps);
        Assert.Equal("active", config.Composer.EventNotifications!.Mode);
        Assert.Equal(["goal_completed"], config.Composer.EventNotifications.ActiveEvents);
        Assert.Equal(45, config.Composer.EventNotifications.ThrottleSeconds);
        Assert.Single(repo.Commits);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_NullEventNotifications_Created()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { MaxSteps = 50 }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(
            new ComposerSettingsUpdate(EventNotificationsMode: "off"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(config.Composer!.EventNotifications);
        Assert.Equal("off", config.Composer.EventNotifications!.Mode);
        Assert.Single(repo.Commits);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_ValidationBeforeMutation_InvalidEventsAfterValidMode_NoMutation()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { MaxSteps = 50 }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        // Mode is valid but events are invalid — the whole update must be rejected
        // before ANY field is applied.
        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.UpdateComposerSettingsAsync(
                new ComposerSettingsUpdate(
                    EventNotificationsMode: "active",
                    EventNotificationsActiveEvents: ["bogus_event"]),
                TestContext.Current.CancellationToken));

        Assert.Null(config.Composer!.EventNotifications);
        Assert.Equal(50, config.Composer.MaxSteps);
        Assert.Empty(repo.Commits);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_SnakeCaseAndPascalCase_Accepted()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(
            new ComposerSettingsUpdate(
                EventNotificationsMode: "ACTIVE",
                EventNotificationsActiveEvents: ["GoalCompleted", "CI_FAILED", "issue_raised"]),
            TestContext.Current.CancellationToken);

        Assert.NotNull(config.Composer!.EventNotifications);
        Assert.Equal("active", config.Composer.EventNotifications!.Mode);
        Assert.Equal(
            ["goal_completed", "ci_failed", "issue_raised"],
            config.Composer.EventNotifications.ActiveEvents);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_Duplicates_DeduplicatedSilently()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(
            new ComposerSettingsUpdate(
                EventNotificationsActiveEvents: ["goal_completed", "goal_completed", "GoalCompleted", "ci_failed"]),
            TestContext.Current.CancellationToken);

        Assert.NotNull(config.Composer!.EventNotifications);
        Assert.Equal(2, config.Composer.EventNotifications!.ActiveEvents!.Count);
        Assert.Contains("goal_completed", config.Composer.EventNotifications.ActiveEvents);
        Assert.Contains("ci_failed", config.Composer.EventNotifications.ActiveEvents);
    }

    // ── Strict whitelist: only the 18 canonical spellings, case-insensitively ──

    /// <summary>
    /// Each whitelisted event has exactly two canonical spellings — snake_case and PascalCase —
    /// and both (in any casing) resolve to the canonical snake_case form.
    /// </summary>
    [Theory]
    [InlineData("goal_completed", "goal_completed")]
    [InlineData("GOAL_COMPLETED", "goal_completed")]
    [InlineData("Goal_Completed", "goal_completed")]
    [InlineData("GoalCompleted", "goal_completed")]
    [InlineData("goalcompleted", "goal_completed")]
    [InlineData("GOALCOMPLETED", "goal_completed")]
    [InlineData("goal_failed", "goal_failed")]
    [InlineData("GoalFailed", "goal_failed")]
    [InlineData("goalfailed", "goal_failed")]
    [InlineData("ci_failed", "ci_failed")]
    [InlineData("CI_FAILED", "ci_failed")]
    [InlineData("CiFailed", "ci_failed")]
    [InlineData("cifailed", "ci_failed")]
    [InlineData("issue_raised", "issue_raised")]
    [InlineData("IssueRaised", "issue_raised")]
    [InlineData("issueraised", "issue_raised")]
    [InlineData("ci_succeeded", "ci_succeeded")]
    [InlineData("CI_SUCCEEDED", "ci_succeeded")]
    [InlineData("CiSucceeded", "ci_succeeded")]
    [InlineData("cisucceeded", "ci_succeeded")]
    [InlineData("release_completed", "release_completed")]
    [InlineData("ReleaseCompleted", "release_completed")]
    [InlineData("releasecompleted", "release_completed")]
    [InlineData("goal_dispatched", "goal_dispatched")]
    [InlineData("GoalDispatched", "goal_dispatched")]
    [InlineData("goaldispatched", "goal_dispatched")]
    [InlineData("issue_resolved", "issue_resolved")]
    [InlineData("IssueResolved", "issue_resolved")]
    [InlineData("issueresolved", "issue_resolved")]
    public async Task UpdateComposerSettingsAsync_CanonicalSpelling_IsAcceptedAndCanonicalized(
        string input, string expected)
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(
            new ComposerSettingsUpdate(EventNotificationsActiveEvents: [input]),
            TestContext.Current.CancellationToken);

        Assert.Equal([expected], config.Composer!.EventNotifications!.ActiveEvents);
        Assert.Single(repo.Commits);
    }

    /// <summary>
    /// Regression: the validator must NOT strip or collapse underscores before comparing.
    /// Malformed spellings that differ from a canonical form only in underscore structure —
    /// or surrounding whitespace — were previously accepted and silently canonicalized; they
    /// must now be rejected with no mutation and no commit.
    /// </summary>
    [Theory]
    [InlineData("goal__completed")]
    [InlineData("_goal_completed")]
    [InlineData("goal_completed_")]
    [InlineData("goal_com_pleted")]
    [InlineData("_goalcompleted")]
    [InlineData("ci__failed")]
    [InlineData("issue_raised__")]
    [InlineData("not_an_event")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" goal_completed ")]
    public async Task UpdateComposerSettingsAsync_MalformedNearWhitelistName_ThrowsAndDoesNotMutate(string input)
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { MaxSteps = 50 }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.UpdateComposerSettingsAsync(
                new ComposerSettingsUpdate(EventNotificationsActiveEvents: [input]),
                TestContext.Current.CancellationToken));

        Assert.Contains("Invalid active event", ex.Message, StringComparison.Ordinal);
        Assert.Null(config.Composer!.EventNotifications);
        Assert.Equal(50, config.Composer.MaxSteps);
        Assert.Empty(repo.Commits);
    }

    /// <summary>
    /// A malformed entry anywhere in the list rejects the WHOLE update — the valid entries that
    /// precede it must not be partially applied.
    /// </summary>
    [Fact]
    public async Task UpdateComposerSettingsAsync_MalformedEntryAfterValidOnes_RejectsWholeUpdate()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Composer = new ComposerConfig { MaxSteps = 50 }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.UpdateComposerSettingsAsync(
                new ComposerSettingsUpdate(
                    MaxSteps: 99,
                    EventNotificationsMode: "active",
                    EventNotificationsActiveEvents: ["goal_completed", "ci_failed", "goal__completed"]),
                TestContext.Current.CancellationToken));

        Assert.Null(config.Composer!.EventNotifications);
        Assert.Equal(50, config.Composer.MaxSteps);
        Assert.Empty(repo.Commits);
    }

    /// <summary>
    /// All four whitelisted events, mixed between the two canonical spellings, are accepted and
    /// stored in their canonical snake_case form.
    /// </summary>
    [Fact]
    public async Task UpdateComposerSettingsAsync_AllFourMixedSpellings_AreAccepted()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(
            new ComposerSettingsUpdate(
                EventNotificationsActiveEvents: ["GoalCompleted", "goal_failed", "CI_FAILED", "IssueRaised"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["goal_completed", "goal_failed", "ci_failed", "issue_raised"],
            config.Composer!.EventNotifications!.ActiveEvents);
    }

    /// <summary>
    /// All 9 recognized events, mixed between snake_case and PascalCase spellings, are accepted
    /// and canonicalized to snake_case.
    /// </summary>
    [Fact]
    public async Task UpdateComposerSettingsAsync_AllNineEvents_AreAcceptedAndCanonicalized()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(
            new ComposerSettingsUpdate(
                EventNotificationsActiveEvents:
                [
                    "GoalCompleted", "goal_failed", "CiFailed", "ISSUE_RAISED", "package_published",
                    "ci_succeeded", "ReleaseCompleted", "GOAL_DISPATCHED", "issue_resolved",
                ]),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["goal_completed", "goal_failed", "ci_failed", "issue_raised", "package_published",
             "ci_succeeded", "release_completed", "goal_dispatched", "issue_resolved"],
            config.Composer!.EventNotifications!.ActiveEvents);
        Assert.Single(repo.Commits);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_PackagePublished_AcceptedCanonicalizedAndPersisted()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(
            new ComposerSettingsUpdate(
                EventNotificationsActiveEvents: ["PackagePublished"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["package_published"],
            config.Composer!.EventNotifications!.ActiveEvents);
        Assert.Single(repo.Commits);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_PackagePublishedWithOthers_CanonicalizesAll()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(
            new ComposerSettingsUpdate(
                EventNotificationsActiveEvents: ["goal_completed", "PACKAGE_PUBLISHED", "issue_raised"]),
            TestContext.Current.CancellationToken);

        Assert.Equal(
            ["goal_completed", "package_published", "issue_raised"],
            config.Composer!.EventNotifications!.ActiveEvents);
        Assert.Single(repo.Commits);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_PackagePublishTimedOut_RejectedAsNotActiveEvent()
    {
        // PackagePublishTimedOut exists in EventType enum but is NOT a recognized active event.
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.UpdateComposerSettingsAsync(
                new ComposerSettingsUpdate(
                    EventNotificationsActiveEvents: ["package_publish_timed_out"]),
                TestContext.Current.CancellationToken));

        Assert.Contains("Invalid active event", ex.Message, StringComparison.Ordinal);
        // Nothing was persisted — validation runs before mutation.
        Assert.Null(config.Composer);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_ThrottleClamped()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateComposerSettingsAsync(
            new ComposerSettingsUpdate(EventNotificationsThrottleSeconds: 999),
            TestContext.Current.CancellationToken);

        Assert.NotNull(config.Composer!.EventNotifications);
        Assert.Equal(300, config.Composer.EventNotifications!.ThrottleSeconds);

        await svc.UpdateComposerSettingsAsync(
            new ComposerSettingsUpdate(EventNotificationsThrottleSeconds: 0),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, config.Composer.EventNotifications.ThrottleSeconds);
    }

    [Fact]
    public async Task UpdateComposerSettingsAsync_ServiceLockSerializes()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new GatedConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        // First call enters the lock and parks inside CommitFileAsync until we release it.
        var first = svc.UpdateComposerSettingsAsync(
            new ComposerSettingsUpdate(EventNotificationsMode: "active"),
            TestContext.Current.CancellationToken);

        await repo.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Second call must block on the save lock — it cannot mutate or commit yet.
        var second = svc.UpdateComposerSettingsAsync(
            new ComposerSettingsUpdate(EventNotificationsMode: "off"),
            TestContext.Current.CancellationToken);

        var completedEarly = await Task.WhenAny(second, Task.Delay(500, TestContext.Current.CancellationToken));
        Assert.NotSame(second, completedEarly);
        Assert.False(second.IsCompleted, "Second UpdateComposerSettingsAsync must block until the first releases the lock.");
        Assert.Equal(1, repo.CommitCalls);
        // Without the lock the second caller would already have overwritten the singleton here.
        Assert.Equal("active", config.Composer!.EventNotifications!.Mode);

        // Release the first call; only then may the second proceed.
        repo.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, repo.CommitCalls);
        Assert.Equal("off", config.Composer!.EventNotifications!.Mode);
    }

    // ── Clone-triggering tests ─────────────────────────────────────────────────

    [Fact]
    public async Task AddRepositoryAsync_CallsEnsureCloneAsync()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var repoManager = new FakeBrainRepoManager();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, null, repoManager);

        await svc.AddRepositoryAsync("test-repo", "https://github.com/org/repo.git", "main", release: null, ct: TestContext.Current.CancellationToken);

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

        await svc.UpdateRepositoryAsync("existing-repo", "https://new.com/repo.git", "develop", release: null, ct: TestContext.Current.CancellationToken);

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
            svc.AddRepositoryAsync("../../etc", "https://github.com/org/repo.git", "main", release: null, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddRepositoryAsync_RejectsNullUrl()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.AddRepositoryAsync("test-repo", "", "main", release: null, ct: TestContext.Current.CancellationToken));
    }

    // ── Description on available models ──────────────────────────────────────

    [Fact]
    public async Task AddAvailableModelAsync_PersistsDescription()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("model-a", 1000, "Fast and cheap", ct: TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.AvailableModels!);
        Assert.Equal("Fast and cheap", model.Description);
        Assert.Contains(repo.Commits, c => c.File == "hive-config.yaml");
    }

    [Fact]
    public async Task UpdateAvailableModelAsync_PersistsDescription()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = "model-a" }] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateAvailableModelAsync("model-a", null, "Deep reasoning", ct: TestContext.Current.CancellationToken);

        Assert.Equal("Deep reasoning", config.Models!.AvailableModels![0].Description);
    }

    // ── Sub-agent model CRUD ─────────────────────────────────────────────────

    [Fact]
    public async Task AddSubAgentModelAsync_AddsAndCommits()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddSubAgentModelAsync("model-a", 128000, ReasoningEffort.High, "Great for research", TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.SubAgentModels!);
        Assert.Equal("model-a", model.Name);
        Assert.Equal(128000, model.ContextWindow);
        Assert.Equal("high", model.ReasoningEffort);
        Assert.Equal("Great for research", model.Description);
        Assert.Contains(repo.Commits, c => c.Message.Contains("add sub-agent model"));
    }

    [Fact]
    public async Task AddSubAgentModelAsync_Duplicate_Throws()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddSubAgentModelAsync("model-a", null, null, null, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AddSubAgentModelAsync("MODEL-A", null, null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateSubAgentModelAsync_UpdatesFields()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { SubAgentModels = [new ModelEntry { Name = "model-a" }] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateSubAgentModelAsync("model-a", 256000, ReasoningEffort.Low, "Cheap", TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.SubAgentModels!);
        Assert.Equal(256000, model.ContextWindow);
        Assert.Equal("low", model.ReasoningEffort);
        Assert.Equal("Cheap", model.Description);
        Assert.Contains(repo.Commits, c => c.Message.Contains("update sub-agent model"));
    }

    [Fact]
    public async Task UpdateSubAgentModelAsync_NotFound_Throws()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateSubAgentModelAsync("missing", null, null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveSubAgentModelAsync_RemovesAndCommits()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                SubAgentModels = [new ModelEntry { Name = "model-a" }, new ModelEntry { Name = "model-b" }]
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var removed = await svc.RemoveSubAgentModelAsync("MODEL-A", TestContext.Current.CancellationToken);

        Assert.True(removed);
        var remaining = Assert.Single(config.Models!.SubAgentModels!);
        Assert.Equal("model-b", remaining.Name);
        Assert.Contains(repo.Commits, c => c.Message.Contains("remove sub-agent model"));
    }

    [Fact]
    public async Task RemoveSubAgentModelAsync_NotFound_ReturnsFalse()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        Assert.False(await svc.RemoveSubAgentModelAsync("missing", TestContext.Current.CancellationToken));
        Assert.Empty(repo.Commits);
    }

    // ── Persisted YAML content proof ─────────────────────────────────────────

    /// <summary>Reads the hive-config.yaml the service actually wrote to the temp repo.</summary>
    private async Task<string> ReadWrittenYamlAsync() =>
        await File.ReadAllTextAsync(
            Path.Combine(_tempDir, "hive-config.yaml"), TestContext.Current.CancellationToken);

    /// <summary>
    /// The reasoning effort comes exclusively from the explicit request field, and the model
    /// name is persisted plain. A name carrying a legacy suffix is NOT a source of reasoning.
    /// </summary>
    [Fact]
    public async Task AddSubAgentModelAsync_UsesExplicitReasoningEffort_AndPersistsToYaml()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddSubAgentModelAsync("copilot/test-model", 64000, ReasoningEffort.High, "Research helper",
            TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.SubAgentModels!);
        Assert.Equal("copilot/test-model", model.Name);
        Assert.Equal("high", model.ReasoningEffort);

        var yaml = await ReadWrittenYamlAsync();
        Assert.Contains("sub_agent_models:", yaml, StringComparison.Ordinal);
        Assert.Contains("copilot/test-model", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("copilot/test-model:high", yaml, StringComparison.Ordinal);
        Assert.Contains("reasoning_effort: high", yaml, StringComparison.Ordinal);
        Assert.Contains("Research helper", yaml, StringComparison.Ordinal);
    }

    /// <summary>
    /// A model name carrying a legacy <c>:high</c> suffix is stored verbatim and, with no
    /// explicit reasoning effort supplied, no reasoning effort is persisted.
    /// </summary>
    [Fact]
    public async Task AddSubAgentModelAsync_LegacySuffixInName_NotUsedAsReasoningSource()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddSubAgentModelAsync("copilot/test-model:high", 64000, null, "Research helper",
            TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.SubAgentModels!);
        Assert.Equal("copilot/test-model:high", model.Name);
        Assert.Null(model.ReasoningEffort);
    }

    /// <summary>
    /// The enum-valued reasoning effort is persisted in its canonical wire form.
    /// </summary>
    [Theory]
    [InlineData(ReasoningEffort.None, "none")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "extra_high")]
    public async Task AddSubAgentModelAsync_PersistsCanonicalWireForm(ReasoningEffort effort, string expected)
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddSubAgentModelAsync("copilot/test-model", 64000, effort, null,
            TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.SubAgentModels!);
        Assert.Equal(expected, model.ReasoningEffort);
    }

    /// <summary>
    /// A <c>null</c> reasoning effort persists as an absent (null) YAML value.
    /// </summary>
    [Fact]
    public async Task AddSubAgentModelAsync_NullReasoningEffort_PersistsNull()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddSubAgentModelAsync("copilot/test-model", 64000, null, null,
            TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.SubAgentModels!);
        Assert.Null(model.ReasoningEffort);
    }

    [Fact]
    public async Task UpdateSubAgentModelAsync_PersistsUpdatedValuesToYaml()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                SubAgentModels = [new ModelEntry { Name = "model-a", Description = "old desc" }]
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateSubAgentModelAsync("model-a", 256000, ReasoningEffort.Low, "new desc",
            TestContext.Current.CancellationToken);

        var yaml = await ReadWrittenYamlAsync();
        Assert.Contains("sub_agent_models:", yaml, StringComparison.Ordinal);
        Assert.Contains("context_window: 256000", yaml, StringComparison.Ordinal);
        Assert.Contains("reasoning_effort: low", yaml, StringComparison.Ordinal);
        Assert.Contains("new desc", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("old desc", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveSubAgentModelAsync_RemovedEntryIsAbsentFromWrittenYaml()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig
            {
                SubAgentModels =
                [
                    new ModelEntry { Name = "doomed-model" },
                    new ModelEntry { Name = "kept-model" }
                ]
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        Assert.True(await svc.RemoveSubAgentModelAsync("doomed-model", TestContext.Current.CancellationToken));

        var yaml = await ReadWrittenYamlAsync();
        Assert.DoesNotContain("doomed-model", yaml, StringComparison.Ordinal);
        Assert.Contains("kept-model", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateAvailableModelAsync_PersistsDescriptionToYaml()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = "model-a" }] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateAvailableModelAsync("model-a", null, "Deep reasoning workhorse",
            ct: TestContext.Current.CancellationToken);

        var yaml = await ReadWrittenYamlAsync();
        Assert.Contains("description: Deep reasoning workhorse", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddAvailableModelAsync_PersistsDescriptionToYaml()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("model-a", 1000, "Fast and cheap",
            ct: TestContext.Current.CancellationToken);

        var yaml = await ReadWrittenYamlAsync();
        Assert.Contains("description: Fast and cheap", yaml, StringComparison.Ordinal);
    }

    // ── SupportsVision tri-state CRUD ────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task AddAvailableModelAsync_PersistsSupportsVisionTriState(bool? vision)
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddAvailableModelAsync("vision-model", 1000, null, vision, TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.AvailableModels!);
        Assert.Equal(vision, model.SupportsVision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task UpdateAvailableModelAsync_PersistsSupportsVisionTriState(bool? vision)
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = "model-a", SupportsVision = true }] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateAvailableModelAsync("model-a", null, null, vision, TestContext.Current.CancellationToken);

        Assert.Equal(vision, config.Models!.AvailableModels![0].SupportsVision);
    }

    [Fact]
    public async Task UpdateAvailableModelAsync_ExplicitFalse_SurvivesRoundTripThroughService()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = "model-a", SupportsVision = true }] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        // Update to explicit false
        await svc.UpdateAvailableModelAsync("model-a", null, null, false, TestContext.Current.CancellationToken);

        Assert.False(config.Models!.AvailableModels![0].SupportsVision);

        // The written YAML must contain supports_vision: false (not be omitted)
        var yaml = await ReadWrittenYamlAsync();
        Assert.Contains("supports_vision: false", yaml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task AddSubAgentModelAsync_PersistsSupportsVisionTriState(bool? vision)
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig() };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.AddSubAgentModelAsync("sa-model", 128000, null, null, vision, TestContext.Current.CancellationToken);

        var model = Assert.Single(config.Models!.SubAgentModels!);
        Assert.Equal(vision, model.SupportsVision);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task UpdateSubAgentModelAsync_PersistsSupportsVisionTriState(bool? vision)
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { SubAgentModels = [new ModelEntry { Name = "model-a", SupportsVision = true }] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateSubAgentModelAsync("model-a", null, null, null, vision, TestContext.Current.CancellationToken);

        Assert.Equal(vision, config.Models!.SubAgentModels![0].SupportsVision);
    }

    [Fact]
    public async Task UpdateSubAgentModelAsync_ExplicitFalse_SurvivesRoundTripThroughService()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { SubAgentModels = [new ModelEntry { Name = "model-a", SupportsVision = true }] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.UpdateSubAgentModelAsync("model-a", null, null, null, false, TestContext.Current.CancellationToken);

        Assert.False(config.Models!.SubAgentModels![0].SupportsVision);

        var yaml = await ReadWrittenYamlAsync();
        Assert.Contains("supports_vision: false", yaml, StringComparison.Ordinal);
    }

    // ── Per-assignment reasoning effort persistence ──────────────────────────

    private static HiveConfigFile CreateReasoningConfig() => new()
    {
        Orchestrator = new OrchestratorConfig { Model = "orch-model", ReasoningEffort = "low" },
        Composer = new ComposerConfig { Model = "composer-model", ReasoningEffort = "low" },
        Models = new ModelsConfig
        {
            SubAgentModels = [new ModelEntry { Name = "sa-model", ReasoningEffort = "low" }]
        },
        Workers =
        {
            ["coder"] = new WorkerConfig { Model = "coder-model", ReasoningEffort = "low", PremiumReasoningEffort = "low" }
        }
    };

    [Fact]
    public async Task SaveModelConfigAsync_AllReasoningFieldsSet_PersistsEachToCorrectProperty()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            OrchestratorReasoningEffort: ReasoningEffort.High,
            ComposerReasoningEffort: ReasoningEffort.Medium,
            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = ReasoningEffort.ExtraHigh, ["tester"] = ReasoningEffort.None },
            WorkerPremiumReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = ReasoningEffort.Medium },
            SubAgentModelReasoning: new Dictionary<string, ReasoningEffort?> { ["sa-model"] = ReasoningEffort.High });

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("high", config.Orchestrator.ReasoningEffort);
        Assert.Equal("medium", config.Composer!.ReasoningEffort);
        Assert.Equal("extra_high", config.Workers["coder"].ReasoningEffort);
        Assert.Equal("none", config.Workers["tester"].ReasoningEffort);
        Assert.Equal("medium", config.Workers["coder"].PremiumReasoningEffort);
        Assert.Equal("high", config.Models!.SubAgentModels![0].ReasoningEffort);
        Assert.Single(repo.Commits);
    }

    [Fact]
    public async Task SaveModelConfigAsync_NullReasoningFields_LeaveExistingValuesUnchanged()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var update = new ModelConfigUpdate(null, null, null, null, "compact-model");

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("low", config.Orchestrator.ReasoningEffort);
        Assert.Equal("low", config.Composer!.ReasoningEffort);
        Assert.Equal("low", config.Workers["coder"].ReasoningEffort);
        Assert.Equal("low", config.Workers["coder"].PremiumReasoningEffort);
        Assert.Equal("low", config.Models!.SubAgentModels![0].ReasoningEffort);
    }

    [Fact]
    public async Task SaveModelConfigAsync_PresentKeyWithNullValue_IsANoOp()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        // A present dictionary key whose value is null carries no level, so it must leave the
        // persisted value untouched rather than clearing it.
        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = null },
            WorkerPremiumReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = null },
            SubAgentModelReasoning: new Dictionary<string, ReasoningEffort?> { ["sa-model"] = null });

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("low", config.Workers["coder"].ReasoningEffort);
        Assert.Equal("low", config.Workers["coder"].PremiumReasoningEffort);
        Assert.Equal("low", config.Models!.SubAgentModels![0].ReasoningEffort);
    }

    [Fact]
    public async Task SaveModelConfigAsync_NoneReasoning_PersistsTheExplicitNoneLevel()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            OrchestratorReasoningEffort: ReasoningEffort.None,
            ComposerReasoningEffort: ReasoningEffort.None,
            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = ReasoningEffort.None },
            WorkerPremiumReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = ReasoningEffort.None },
            SubAgentModelReasoning: new Dictionary<string, ReasoningEffort?> { ["sa-model"] = ReasoningEffort.None });

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        // None is an explicit level, NOT "unset" — it persists as the "none" wire string.
        Assert.Equal("none", config.Orchestrator.ReasoningEffort);
        Assert.Equal("none", config.Composer!.ReasoningEffort);
        Assert.Equal("none", config.Workers["coder"].ReasoningEffort);
        Assert.Equal("none", config.Workers["coder"].PremiumReasoningEffort);
        Assert.Equal("none", config.Models!.SubAgentModels![0].ReasoningEffort);
    }

    [Fact]
    public async Task SaveModelConfigAsync_UnknownWorkerReasoningKeys_AreIgnoredWithoutError()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["merger"] = ReasoningEffort.High },
            WorkerPremiumReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["ghost"] = ReasoningEffort.High },
            SubAgentModelReasoning: new Dictionary<string, ReasoningEffort?> { ["unknown-model"] = ReasoningEffort.High });

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.False(config.Workers.ContainsKey("merger"));
        Assert.False(config.Workers.ContainsKey("ghost"));
        Assert.Equal("low", config.Workers["coder"].ReasoningEffort);
        Assert.Equal("low", config.Models!.SubAgentModels![0].ReasoningEffort);
        Assert.Single(repo.Commits);
    }

    [Fact]
    public async Task SaveModelConfigAsync_UnknownWorkerReasoningKey_IsCaseInsensitiveForKnownRoles()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["DocWriter"] = ReasoningEffort.High },
            SubAgentModelReasoning: new Dictionary<string, ReasoningEffort?> { ["SA-MODEL"] = ReasoningEffort.High });

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("high", config.Workers["docwriter"].ReasoningEffort);
        Assert.Equal("high", config.Models!.SubAgentModels![0].ReasoningEffort);
    }

    [Fact]
    public async Task SaveModelConfigAsync_ModelAndReasoningChanged_SendsSingleBrainUpdateWithBothValues()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "old-model", ReasoningEffort = "low" },
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "new-orch", ContextWindow = 256000 }]
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);

        var update = new ModelConfigUpdate(
            "new-orch", null, null, null, null,
            OrchestratorReasoningEffort: ReasoningEffort.High);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal(1, brain.UpdateModelCalls);
        Assert.Equal(1, brain.ReasoningCaptureCalls);
        Assert.Equal("new-orch", brain.LastModel);
        Assert.Equal(256000, brain.LastMaxContextTokens);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.High, brain.LastReasoningEffort);
        Assert.Equal("high", config.Orchestrator.ReasoningEffort);
    }

    [Fact]
    public async Task SaveModelConfigAsync_ReasoningOnlyChange_UpdatesBrainWithCurrentModel()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "current-orch" },
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "current-orch", ContextWindow = 111000 }]
            }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);

        var update = new ModelConfigUpdate(null, null, null, null, null, OrchestratorReasoningEffort: ReasoningEffort.Medium);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal(1, brain.UpdateModelCalls);
        Assert.Equal("current-orch", brain.LastModel);
        Assert.Equal(111000, brain.LastMaxContextTokens);
        Assert.Equal(Microsoft.Extensions.AI.ReasoningEffort.Medium, brain.LastReasoningEffort);
    }

    [Fact]
    public async Task SaveModelConfigAsync_NoneOrchestratorReasoning_SendsNoneReasoningToBrain()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "current-orch", ReasoningEffort = "high" }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);

        var update = new ModelConfigUpdate(null, null, null, null, null, OrchestratorReasoningEffort: ReasoningEffort.None);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal(1, brain.UpdateModelCalls);
        Assert.Equal(ReasoningEffort.None, brain.LastReasoningEffort);
        Assert.Equal("none", config.Orchestrator.ReasoningEffort);
    }

    [Fact]
    public async Task SaveModelConfigAsync_ComposerOnlyReasoningChange_DoesNotUpdateBrain()
    {
        // Composer reasoning is persistence-only — no live Brain update may happen.
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);

        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            ComposerReasoningEffort: ReasoningEffort.High);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("high", config.Composer!.ReasoningEffort);
        Assert.Single(repo.Commits);
        Assert.Equal(0, brain.UpdateModelCalls);
        Assert.Null(brain.LastModel);
    }

    [Fact]
    public async Task SaveModelConfigAsync_PersistenceFails_DoesNotCallBrainAndRethrows()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig { Model = "old-model" } };
        var repo = new ThrowingConfigRepoManager(
            "https://example.com/config.git", _tempDir, new IOException("disk on fire"));
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);

        var update = new ModelConfigUpdate("new-orch", null, null, null, null, OrchestratorReasoningEffort: ReasoningEffort.High);

        await Assert.ThrowsAsync<IOException>(() =>
            svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken));

        Assert.Equal(1, repo.CommitAttempts);
        Assert.Equal(0, brain.UpdateModelCalls);
        Assert.Null(brain.LastModel);
    }

    [Fact]
    public async Task SaveModelConfigAsync_LiveBrainUpdateFails_SuppressesExceptionAndReturns()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig { Model = "old-model" } };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain { UpdateModelException = new InvalidOperationException("brain down") };
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);

        var update = new ModelConfigUpdate("new-orch", null, null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal(1, brain.UpdateModelCalls);
        Assert.Single(repo.Commits);
        Assert.Equal("new-orch", config.Orchestrator.Model);
    }

    [Fact]
    public async Task SaveModelConfigAsync_CancelledToken_PropagatesOperationCanceledException()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig { Model = "old-model" } };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var update = new ModelConfigUpdate("new-orch", null, null, null, null, OrchestratorReasoningEffort: ReasoningEffort.High);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.SaveModelConfigAsync(update, cts.Token));

        // The save lock honours the token, so a cancelled request never mutates the singleton.
        Assert.Equal("old-model", config.Orchestrator.Model);
        Assert.Null(config.Orchestrator.ReasoningEffort);
        Assert.Equal(0, brain.UpdateModelCalls);
        Assert.Empty(repo.Commits);
    }

    [Fact]
    public async Task SaveModelConfigAsync_CommitThrowsOperationCanceled_PropagatesAndSkipsBrainUpdate()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig { Model = "old-model" } };
        var repo = new ThrowingConfigRepoManager(
            "https://example.com/config.git", _tempDir, new OperationCanceledException("commit cancelled"));
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);

        var update = new ModelConfigUpdate("new-orch", null, null, null, null, OrchestratorReasoningEffort: ReasoningEffort.High);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken));

        Assert.Equal(1, repo.CommitAttempts);
        Assert.Equal(0, brain.UpdateModelCalls);
    }

    [Fact]
    public async Task SaveModelConfigAsync_WriteThrows_DoesNotCallBrainAndRethrows()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig { Model = "old-model" } };
        // Point the repo at a non-existent directory so WriteConfigAsync's File.WriteAllTextAsync
        // throws before any commit or live-update can occur.
        var repo = new ConfigRepoManager(
            "https://example.com/config.git", Path.Combine(_tempDir, "does-not-exist"));
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);

        var update = new ModelConfigUpdate("new-orch", null, null, null, null, OrchestratorReasoningEffort: ReasoningEffort.High);

        await Assert.ThrowsAnyAsync<IOException>(() =>
            svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken));

        // The in-memory singleton was mutated (existing behavior) but the brain was never called
        // because persistence failed before the live-update step.
        Assert.Equal("new-orch", config.Orchestrator.Model);
        Assert.Equal(0, brain.UpdateModelCalls);
    }

    [Fact]
    public async Task SaveModelConfigAsync_BrainUpdateThrowsOperationCanceled_PropagatesAfterPersistence()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig { Model = "old-model" } };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain { UpdateModelException = new OperationCanceledException("brain cancelled") };
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);

        var update = new ModelConfigUpdate("new-orch", null, null, null, null, OrchestratorReasoningEffort: ReasoningEffort.High);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken));

        // Persistence succeeded before the brain update was attempted and its OCE propagated.
        Assert.Equal(1, brain.UpdateModelCalls);
        Assert.Single(repo.Commits);
        Assert.Equal("new-orch", config.Orchestrator.Model);
    }

    [Fact]
    public async Task SaveModelConfigAsync_LiveTokenCancelledDuringCommit_PropagatesOperationCanceledException()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig { Model = "old-model" } };
        var repo = new GatedCommitConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);

        using var cts = new CancellationTokenSource();
        var update = new ModelConfigUpdate("new-orch", null, null, null, null, OrchestratorReasoningEffort: ReasoningEffort.High);

        var saveTask = svc.SaveModelConfigAsync(update, cts.Token);

        // Wait for the commit to be entered (persistence has started).
        await repo.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Cancel the live token while the commit is blocked on the gate.
        await cts.CancelAsync();

#pragma warning disable xUnit1051 // Timeout-only WaitAsync is intentional: a timeout must surface as TimeoutException, not OCE
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => saveTask.WaitAsync(TimeSpan.FromSeconds(10)));
#pragma warning restore xUnit1051

        Assert.Equal(0, brain.UpdateModelCalls);
        Assert.Empty(repo.Commits);
    }

    [Fact]
    public async Task SaveModelConfigAsync_LiveTokenCancelledDuringBrainUpdate_PropagatesOperationCanceledException()
    {
        var config = new HiveConfigFile { Orchestrator = new OrchestratorConfig { Model = "old-model" } };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new GatedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);

        using var cts = new CancellationTokenSource();
        var update = new ModelConfigUpdate("new-orch", null, null, null, null, OrchestratorReasoningEffort: ReasoningEffort.High);

        var saveTask = svc.SaveModelConfigAsync(update, cts.Token);

        // Wait for the live Brain update to be entered (persistence already succeeded).
        await brain.UpdateEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Cancel the live token while the brain update is blocked.
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => saveTask);

        // Persistence completed; the OCE from the brain update propagated.
        Assert.Equal(1, brain.UpdateModelCalls);
        Assert.Single(repo.Commits);
        Assert.Equal("new-orch", config.Orchestrator.Model);
    }

    [Fact]
    public void Description_ReasoningChanges_AppearInSummary()
    {
        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            OrchestratorReasoningEffort: ReasoningEffort.High,
            ComposerReasoningEffort: ReasoningEffort.None,
            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = ReasoningEffort.Low });

        Assert.Contains("orchestrator reasoning→high", update.Description, StringComparison.Ordinal);
        Assert.Contains("composer reasoning→none", update.Description, StringComparison.Ordinal);
        Assert.Contains("worker reasoning: coder→low", update.Description, StringComparison.Ordinal);
    }

    // ── Enum-typed API contract: no string parsing remains ───────────────────

    [Theory]
    [InlineData(ReasoningEffort.None, "none")]
    [InlineData(ReasoningEffort.Low, "low")]
    [InlineData(ReasoningEffort.Medium, "medium")]
    [InlineData(ReasoningEffort.High, "high")]
    [InlineData(ReasoningEffort.ExtraHigh, "extra_high")]
    public async Task SaveModelConfigAsync_OrchestratorReasoning_PersistsCanonicalWireForm(
        ReasoningEffort input, string expected)
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await svc.SaveModelConfigAsync(
            new ModelConfigUpdate(null, null, null, null, null, OrchestratorReasoningEffort: input),
            TestContext.Current.CancellationToken);

        Assert.Equal(expected, config.Orchestrator.ReasoningEffort);
    }

    // ── ParseLenient ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void ParseLenient_NullEmptyOrWhitespace_ReturnsNull(string? value)
    {
        Assert.Null(ConfigModelService.ParseLenient(value));
    }

    [Theory]
    [InlineData("turbo")]
    [InlineData("HIGHEST")]
    [InlineData("1")]
    public void ParseLenient_InvalidValue_ReturnsNullInsteadOfThrowing(string value)
    {
        Assert.Null(ConfigModelService.ParseLenient(value));
    }

    [Theory]
    [InlineData("none", ReasoningEffort.None)]
    [InlineData("low", ReasoningEffort.Low)]
    [InlineData("medium", ReasoningEffort.Medium)]
    [InlineData("High", ReasoningEffort.High)]
    [InlineData("  EXTRA_HIGH ", ReasoningEffort.ExtraHigh)]
    public void ParseLenient_ValidValue_ReturnsEnum(string value, ReasoningEffort expected)
    {
        Assert.Equal(expected, ConfigModelService.ParseLenient(value));
    }

    // ── Fix 1: every reasoning category persists in canonical wire form ──────

    [Fact]
    public async Task SaveModelConfigAsync_AllReasoningCategories_PersistCanonicalWireForms()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            OrchestratorReasoningEffort: ReasoningEffort.High,
            ComposerReasoningEffort: ReasoningEffort.Medium,
            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = ReasoningEffort.ExtraHigh },
            WorkerPremiumReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = ReasoningEffort.Low },
            SubAgentModelReasoning: new Dictionary<string, ReasoningEffort?> { ["sa-model"] = ReasoningEffort.None });

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("high", config.Orchestrator.ReasoningEffort);
        Assert.Equal("medium", config.Composer!.ReasoningEffort);
        Assert.Equal("extra_high", config.Workers["coder"].ReasoningEffort);
        Assert.Equal("low", config.Workers["coder"].PremiumReasoningEffort);
        Assert.Equal("none", config.Models!.SubAgentModels![0].ReasoningEffort);
    }

    [Fact]
    public async Task SaveModelConfigAsync_OuterNullReasoning_LeavesEveryCategoryUntouched()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        // Outer null (field absent) is a no-op for every category.
        await svc.SaveModelConfigAsync(
            new ModelConfigUpdate(null, null, null, null, "compact-model"),
            TestContext.Current.CancellationToken);

        Assert.Equal("low", config.Orchestrator.ReasoningEffort);
        Assert.Equal("low", config.Composer!.ReasoningEffort);
        Assert.Equal("low", config.Workers["coder"].ReasoningEffort);
        Assert.Equal("low", config.Workers["coder"].PremiumReasoningEffort);
        Assert.Equal("low", config.Models!.SubAgentModels![0].ReasoningEffort);

        // A no-op assignment writes nothing new — the pre-existing values survive the round trip.
        var yaml = await ReadWrittenYamlAsync();
        Assert.Contains("reasoning_effort: low", yaml, StringComparison.Ordinal);
    }

    // ── Fix 2: case-insensitive duplicate keys are rejected ──────────────────

    [Fact]
    public async Task SaveModelConfigAsync_WorkerReasoningDuplicateCaseKeys_ThrowsAndDoesNotMutate()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["Coder"] = ReasoningEffort.High, ["coder"] = ReasoningEffort.Low });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken));

        Assert.Contains("duplicate case-insensitive key", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Coder", ex.Message, StringComparison.Ordinal);
        Assert.Equal("low", config.Workers["coder"].ReasoningEffort);
        Assert.Empty(repo.Commits);
    }

    [Fact]
    public async Task SaveModelConfigAsync_WorkerPremiumReasoningDuplicateCaseKeys_Throws()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            WorkerPremiumReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["TESTER"] = ReasoningEffort.High, ["tester"] = ReasoningEffort.Low });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken));

        Assert.Empty(repo.Commits);
    }

    [Fact]
    public async Task SaveModelConfigAsync_SubAgentReasoningDuplicateCaseKeys_Throws()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig(),
            Models = new ModelsConfig { SubAgentModels = [new ModelEntry { Name = "Model-A", ReasoningEffort = "low" }] }
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            SubAgentModelReasoning: new Dictionary<string, ReasoningEffort?> { ["Model-A"] = ReasoningEffort.High, ["model-a"] = ReasoningEffort.Low });

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken));

        Assert.Contains("duplicate case-insensitive key", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("low", config.Models!.SubAgentModels![0].ReasoningEffort);
        Assert.Empty(repo.Commits);
    }

    [Fact]
    public async Task SaveModelConfigAsync_DuplicateCaseKeysWithConflictingValues_ThrowsInsteadOfPickingOne()
    {
        // Regression: which of the two conflicting values wins must never depend on the order
        // the JSON properties were inserted in — the whole update is rejected instead.
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = ReasoningEffort.High, ["CODER"] = ReasoningEffort.Medium });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken));

        Assert.Equal("low", config.Workers["coder"].ReasoningEffort);
        Assert.Empty(repo.Commits);
    }

    [Fact]
    public async Task SaveModelConfigAsync_DuplicateCaseKeysForUnknownRole_AreStillIgnored()
    {
        // Duplicates only matter for known keys; unknown keys are ignored entirely.
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["Ghost"] = ReasoningEffort.High, ["ghost"] = ReasoningEffort.Low });

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.False(config.Workers.ContainsKey("ghost"));
        Assert.Single(repo.Commits);
    }

    // ── Fix 3: the save transaction is serialized ────────────────────────────

    [Fact]
    public async Task SaveModelConfigAsync_ConcurrentCalls_AreSerializedBySaveLock()
    {
        var config = CreateReasoningConfig();
        var repo = new GatedConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        // First call enters the lock and parks inside CommitFileAsync until we release it.
        var first = svc.SaveModelConfigAsync(
            new ModelConfigUpdate(null, null, null, null, null, OrchestratorReasoningEffort: ReasoningEffort.High),
            TestContext.Current.CancellationToken);

        await repo.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Second call must block on the save lock — it cannot mutate or commit yet.
        var second = svc.SaveModelConfigAsync(
            new ModelConfigUpdate(null, null, null, null, null, OrchestratorReasoningEffort: ReasoningEffort.Low),
            TestContext.Current.CancellationToken);

        var completedEarly = await Task.WhenAny(second, Task.Delay(500, TestContext.Current.CancellationToken));
        Assert.NotSame(second, completedEarly);
        Assert.False(second.IsCompleted, "Second SaveModelConfigAsync must block until the first releases the lock.");
        Assert.Equal(1, repo.CommitCalls);
        // Without the lock the second caller would already have overwritten the singleton here.
        Assert.Equal("high", config.Orchestrator.ReasoningEffort);

        // Release the first call; only then may the second proceed.
        repo.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(2, repo.CommitCalls);
        Assert.Equal("low", config.Orchestrator.ReasoningEffort);
    }

    [Fact]
    public async Task SaveModelConfigAsync_LockIsReleasedAfterFailure_SoLaterCallsSucceed()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => svc.SaveModelConfigAsync(
            new ModelConfigUpdate(
                null, null, null, null, null,
                WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?>
                {
                    ["coder"] = ReasoningEffort.High,
                    ["CODER"] = ReasoningEffort.Medium,
                }),
            TestContext.Current.CancellationToken));

        // The finally block must have released the semaphore.
        await svc.SaveModelConfigAsync(
            new ModelConfigUpdate(null, null, null, null, null, OrchestratorReasoningEffort: ReasoningEffort.High),
            TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("high", config.Orchestrator.ReasoningEffort);
    }

    // ── Fix 4: persisted-YAML regression coverage ────────────────────────────

    [Fact]
    public async Task SaveModelConfigAsync_WritesAllReasoningDimensionsToYamlFile()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            OrchestratorReasoningEffort: ReasoningEffort.High,
            ComposerReasoningEffort: ReasoningEffort.Medium,
            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = ReasoningEffort.Low },
            WorkerPremiumReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = ReasoningEffort.ExtraHigh },
            SubAgentModelReasoning: new Dictionary<string, ReasoningEffort?> { ["sa-model"] = ReasoningEffort.None });

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        var yaml = await ReadWrittenYamlAsync();

        // Every reasoning dimension must actually reach the file, in canonical lowercase form.
        Assert.Contains("reasoning_effort: high", yaml, StringComparison.Ordinal);
        Assert.Contains("reasoning_effort: medium", yaml, StringComparison.Ordinal);
        Assert.Contains("reasoning_effort: low", yaml, StringComparison.Ordinal);
        Assert.Contains("premium_reasoning_effort: extra_high", yaml, StringComparison.Ordinal);
        Assert.Contains("reasoning_effort: none", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("reasoning_effort: High", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveModelConfigAsync_ReasoningValues_SurviveYamlRoundTrip()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            OrchestratorReasoningEffort: ReasoningEffort.High,
            ComposerReasoningEffort: ReasoningEffort.Medium,
            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = ReasoningEffort.Low },
            WorkerPremiumReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = ReasoningEffort.ExtraHigh },
            SubAgentModelReasoning: new Dictionary<string, ReasoningEffort?> { ["sa-model"] = ReasoningEffort.None });

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        // Re-read the file through the production parser — proves the write path serialized
        // every field and the read path can recover it.
        var reloaded = ConfigRepoManager.ParseConfig(await ReadWrittenYamlAsync());

        Assert.Equal("high", reloaded.Orchestrator.ReasoningEffort);
        Assert.Equal("medium", reloaded.Composer!.ReasoningEffort);
        Assert.Equal("low", reloaded.Workers["coder"].ReasoningEffort);
        Assert.Equal("extra_high", reloaded.Workers["coder"].PremiumReasoningEffort);
        Assert.Equal("none", reloaded.Models!.SubAgentModels![0].ReasoningEffort);
        Assert.Empty(reloaded.ValidateReasoningEffort());
    }

    [Fact]
    public async Task SaveModelConfigAsync_NullReasoningValues_LeavePersistedYamlUntouched()
    {
        var config = CreateReasoningConfig();
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance);

        // Null carries no level: neither the outer field nor a present-key-null entry may
        // change what is persisted.
        var update = new ModelConfigUpdate(
            null, null, null, null, null,
            WorkerReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = null },
            WorkerPremiumReasoningEffort: new Dictionary<string, ReasoningEffort?> { ["coder"] = null },
            SubAgentModelReasoning: new Dictionary<string, ReasoningEffort?> { ["sa-model"] = null });

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        var reloaded = ConfigRepoManager.ParseConfig(await ReadWrittenYamlAsync());
        Assert.Equal("low", reloaded.Orchestrator.ReasoningEffort);
        Assert.Equal("low", reloaded.Composer!.ReasoningEffort);
        Assert.Equal("low", reloaded.Workers["coder"].ReasoningEffort);
        Assert.Equal("low", reloaded.Workers["coder"].PremiumReasoningEffort);
        Assert.Equal("low", reloaded.Models!.SubAgentModels![0].ReasoningEffort);
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

    /// <summary>Reasoning effort captured from UpdateModelAsync.</summary>
    public Microsoft.Extensions.AI.ReasoningEffort? LastReasoningEffort { get; private set; }

    /// <summary>Number of UpdateModelAsync calls that captured reasoning effort.</summary>
    public int ReasoningCaptureCalls { get; private set; }

    /// <summary>Total number of UpdateModelAsync calls.</summary>
    public int UpdateModelCalls { get; private set; }

    /// <summary>When set, UpdateModelAsync throws this exception.</summary>
    public Exception? UpdateModelException { get; set; }

    public Task ConnectAsync(CancellationToken ct = default) { Connected = true; return Task.CompletedTask; }

    public Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct)
    {
        ReasoningCaptureCalls++;
        LastReasoningEffort = reasoningEffort;
        UpdateModelCalls++;
        LastModel = model;
        LastMaxContextTokens = maxContextTokens;
        if (UpdateModelException is not null)
            throw UpdateModelException;
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

    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

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
    public Task<string?> MergeBranchAsync(string repoName, string sourceBranch, string targetBranch, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);
    public Task<bool> CreateTagAsync(string repoName, string tag, string branch, string message, CancellationToken ct = default) =>
        Task.FromResult(false);
    public Task<bool> DeleteTagAsync(string repoName, string tag, CancellationToken ct = default) =>
        Task.FromResult(false);
    public Task<List<string>> ListRemoteBranchesAsync(string repoName, CancellationToken ct = default) =>
        Task.FromResult(new List<string>());
}

/// <summary>
/// Config repo fake whose <see cref="CommitFileAsync"/> throws a configurable exception,
/// used to exercise the persistence-failure path of <see cref="ConfigModelService"/>.
/// </summary>
file sealed class ThrowingConfigRepoManager(string url, string path, Exception toThrow)
    : ConfigRepoManager(url, path)
{
    public int CommitAttempts { get; private set; }

    public override Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        CommitAttempts++;
        throw toThrow;
    }
}

/// <summary>
/// Config repo fake whose <see cref="CommitFileAsync"/> blocks until the test releases it,
/// enabling live-token cancellation to race the commit step.
/// </summary>
file sealed class GatedCommitConfigRepoManager(string url, string path) : ConfigRepoManager(url, path)
{
    public TaskCompletionSource<bool> CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource<bool> ReleaseGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<(string File, string Message)> Commits { get; } = [];

    public override async Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        CommitEntered.TrySetResult(true);
        await ReleaseGate.Task.WaitAsync(ct);
        Commits.Add((filePath, commitMessage));
    }
}

/// <summary>
/// Brain fake whose reasoning-aware <see cref="GatedBrain.UpdateModelAsync(string, int?, Microsoft.Extensions.AI.ReasoningEffort?, CancellationToken)"/>
/// blocks until the test cancels the live token, enabling live-token cancellation to race the live-update step.
/// </summary>
file sealed class GatedBrain : IDistributedBrain
{
    public TaskCompletionSource<bool> UpdateEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int UpdateModelCalls { get; private set; }
    public string? LastModel { get; private set; }

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async Task UpdateModelAsync(string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort, CancellationToken ct)
    {
        UpdateModelCalls++;
        LastModel = model;
        UpdateEntered.TrySetResult(true);
        // Block until the caller's token is cancelled — Task.Delay(Timeout.Infinite, ct)
        // throws OperationCanceledException on cancellation.
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    public Task<PlanResult> PlanIterationAsync(GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default)
        => Task.FromResult(PlanResult.Success(IterationPlan.Default()));

    public Task<PromptResult> CraftPromptAsync(GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default)
        => Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

    public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task EnsureBrainRepoAsync(string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct)
        => Task.CompletedTask;

    public Task<BrainResponse> AskQuestionAsync(string goalId, int iteration, string phase, string workerRole, string question, CancellationToken ct = default)
        => Task.FromResult(BrainResponse.Answer("n/a"));

    public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;
    public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;
    public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;
    public bool GoalSessionExists(string goalId) => false;

    public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default)
        => Task.FromResult("done.");

    public BrainStats? GetStats() => null;
}

/// <summary>
/// Config repo fake whose first <see cref="CommitFileAsync"/> call parks until explicitly
/// released, used to prove <see cref="ConfigModelService.SaveModelConfigAsync"/> serialises
/// concurrent callers. The commit happens inside the save transaction, so while the first
/// caller is parked the second caller must still be waiting on the save lock.
/// </summary>
file sealed class GatedConfigRepoManager(string url, string path) : ConfigRepoManager(url, path)
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _commitCalls;

    /// <summary>Completes as soon as the first commit call has entered the critical section.</summary>
    public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Number of commit calls observed.</summary>
    public int CommitCalls => Volatile.Read(ref _commitCalls);

    /// <summary>Unblocks the parked first commit call.</summary>
    public void Release() => _gate.TrySetResult();

    public override async Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        var call = Interlocked.Increment(ref _commitCalls);
        if (call == 1)
        {
            CommitEntered.TrySetResult();
            await _gate.Task.WaitAsync(ct);
        }
    }
}

/// <summary>
/// Endpoint-level coverage proving the PATCH /api/config/models handler binds the HTTP request
/// <see cref="CancellationToken"/> and forwards it into <see cref="ConfigModelService"/>, so a
/// client abort can actually cancel the write/commit/live-update sequence.
/// </summary>
[Collection("HiveIntegration")]
public sealed class ConfigModelsPatchCancellationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TokenCapturingConfigRepoManager _repo;
    private readonly PatchCancellationFactory _factory;
    private readonly HttpClient _client;

    public ConfigModelsPatchCancellationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"copilothive-patchct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _repo = new TokenCapturingConfigRepoManager("https://example.com/config.git", _tempDir);
        _factory = new PatchCancellationFactory(_tempDir, _repo);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        if (Directory.Exists(_tempDir))
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task PatchModels_ForwardsLiveRequestCancellationToken_NotNone()
    {
        var response = await _client.PatchAsJsonAsync(
            "/api/config/models",
            new { orchestratorReasoningEffort = "high" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // A dropped token would surface here as default(CancellationToken).
        Assert.True(_repo.TokenObserved, "CommitFileAsync was never reached");
        Assert.NotEqual(CancellationToken.None, _repo.LastToken);
        Assert.True(_repo.LastToken.CanBeCanceled,
            "The PATCH handler must forward the request's cancellable token, not CancellationToken.None");
    }

    [Fact]
    public async Task PatchModels_InvalidReasoningValue_Returns400()
    {
        // Goal contract: reasoning effort is a ReasoningEffort? enum, so an unknown wire value
        // is rejected by the global JSON enum converter and produces a 400 — never a 500.
        var response = await _client.PatchAsJsonAsync(
            "/api/config/models",
            new { orchestratorReasoningEffort = "turbo" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task PatchModels_IntegerReasoningValue_Returns400()
    {
        // allowIntegerValues: false — a numeric level can never be silently coerced.
        var response = await _client.PatchAsJsonAsync(
            "/api/config/models",
            new { orchestratorReasoningEffort = 3 },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetModels_ProjectsReasoningEffortAsSnakeCaseEnum()
    {
        await _client.PatchAsJsonAsync(
            "/api/config/models",
            new { orchestratorReasoningEffort = "extra_high" },
            TestContext.Current.CancellationToken);

        var response = await _client.GetAsync("/api/config/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("extra_high",
            doc.RootElement.GetProperty("orchestratorReasoningEffort").GetString());
    }

    [Fact]
    public async Task GetModels_ExposesEveryReasoningDimension()
    {
        var response = await _client.GetAsync("/api/config/models", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = await System.Text.Json.JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(TestContext.Current.CancellationToken),
            cancellationToken: TestContext.Current.CancellationToken);

        foreach (var field in new[]
                 {
                     "orchestratorReasoningEffort",
                     "composerReasoningEffort",
                     "workerReasoningEffort",
                     "workerPremiumReasoningEffort",
                     "subAgentModelReasoning",
                 })
        {
            Assert.True(doc.RootElement.TryGetProperty(field, out _),
                $"GET /api/config/models must expose '{field}'");
        }
    }

    [Fact]
    public async Task PatchModels_ClientAborts_CancelsTheInFlightCommit()
    {
        _repo.BlockUntilCancelled = true;

        using var cts = new CancellationTokenSource();
        var request = _client.PatchAsJsonAsync(
            "/api/config/models",
            new { orchestratorReasoningEffort = "high" },
            cts.Token);

        // Wait until the server is genuinely inside the commit before aborting.
        await _repo.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        await cts.CancelAsync();

        // The forwarded token — not just the client socket — must be signalled server-side.
        // If the endpoint dropped the token this wait times out and the test fails.
        await _repo.CommitCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(_repo.CommitCancelled.Task.IsCompletedSuccessfully,
            "The PATCH handler must forward the request token so an abort cancels the in-flight commit.");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }
}

/// <summary>
/// Config repo fake that records the <see cref="CancellationToken"/> handed to
/// <see cref="CommitFileAsync"/> and can park there until that token is cancelled.
/// </summary>
internal sealed class TokenCapturingConfigRepoManager(string url, string path) : ConfigRepoManager(url, path)
{
    /// <summary>The token observed by the most recent commit call.</summary>
    public CancellationToken LastToken { get; private set; }

    /// <summary>Whether a commit call was observed at all.</summary>
    public bool TokenObserved { get; private set; }

    /// <summary>When true, the commit parks until the forwarded token is cancelled.</summary>
    public bool BlockUntilCancelled { get; set; }

    /// <summary>Completes when a blocking commit call has been entered.</summary>
    public TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the forwarded token was observed as cancelled inside the commit.</summary>
    public TaskCompletionSource CommitCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public override async Task CommitFileAsync(string filePath, string commitMessage, CancellationToken ct = default)
    {
        LastToken = ct;
        TokenObserved = true;

        if (!BlockUntilCancelled)
            return;

        CommitEntered.TrySetResult();
        try
        {
            // Bounded rather than infinite: if the endpoint ever drops the token the request
            // still completes, so the test fails on its assertion instead of hanging the host.
            await Task.Delay(TimeSpan.FromSeconds(15), ct);
        }
        catch (OperationCanceledException)
        {
            CommitCancelled.TrySetResult();
            throw;
        }
    }
}

/// <summary>
/// Boots the app with a <see cref="ConfigModelService"/> backed by a token-capturing config repo.
/// </summary>
internal sealed class PatchCancellationFactory : WebApplicationFactory<Program>
{
    private readonly string? _previousStateDir;
    private readonly HiveConfigFile _config;
    private readonly TokenCapturingConfigRepoManager _repo;

    public PatchCancellationFactory(string tempDir, TokenCapturingConfigRepoManager repo)
    {
        _previousStateDir = Environment.GetEnvironmentVariable("STATE_DIR");
        Environment.SetEnvironmentVariable("STATE_DIR", Path.Combine(tempDir, "state"));
        _config = new HiveConfigFile { Orchestrator = new OrchestratorConfig { Model = "orch-model" } };
        _repo = repo;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Environment.SetEnvironmentVariable("STATE_DIR", _previousStateDir);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(_config);
            services.AddSingleton<ConfigRepoManager>(_repo);
            services.AddSingleton<ConfigModelService>();
        });
    }
}
