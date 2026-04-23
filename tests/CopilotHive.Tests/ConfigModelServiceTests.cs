using CopilotHive.Configuration;
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
    public async Task SaveModelConfigAsync_ModelNotInAvailableModels_FallsBackToBrainContextWindow()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "old-model", BrainContextWindow = 128000 },
        };
        var repo = new FakeConfigRepoManager("https://example.com/config.git", _tempDir);
        var brain = new FakeDistributedBrain();
        var svc = new ConfigModelService(config, repo, NullLogger<ConfigModelService>.Instance, brain);
        var update = new ModelConfigUpdate("unknown-model", null, null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Equal("unknown-model", brain.LastModel);
        Assert.Equal(128000, brain.LastMaxContextTokens);
    }

    [Fact]
    public async Task SaveModelConfigAsync_NeitherLookupYieldsValue_PassesDefaultBrainContextWindow()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "old-model", BrainContextWindow = 0 },
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
