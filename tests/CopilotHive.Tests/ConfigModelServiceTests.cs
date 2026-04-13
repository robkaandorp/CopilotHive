using CopilotHive.Configuration;
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
        var update = new ModelConfigUpdate("new-orch", null, null, null);

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
        var update = new ModelConfigUpdate(null, "new-composer", null, null);

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
        var update = new ModelConfigUpdate(null, null, new Dictionary<string, string> { ["coder"] = "special-model" }, null);

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
        var update = new ModelConfigUpdate(null, null, null, "compact-model");

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
        var update = new ModelConfigUpdate("orch", null, null, null);

        await svc.SaveModelConfigAsync(update, TestContext.Current.CancellationToken);

        Assert.Single(repo.Commits);
        Assert.Equal("hive-config.yaml", repo.Commits[0].File);
        Assert.StartsWith("chore: update model configuration", repo.Commits[0].Message);
    }

    // ── Test 6: ModelConfigUpdate.Description formats correctly ──────────────

    [Fact]
    public void Description_OrchestratorOnly_ContainsOrchestratorOnly()
    {
        var update = new ModelConfigUpdate("orch", null, null, null);
        Assert.Contains("orchestrator→orch", update.Description);
        Assert.DoesNotContain("composer", update.Description);
        Assert.DoesNotContain("compaction", update.Description);
    }

    [Fact]
    public void Description_ComposerAndCompaction_ContainsBoth()
    {
        var update = new ModelConfigUpdate(null, "comp", null, "mini");
        Assert.Contains("composer→comp", update.Description);
        Assert.Contains("compaction→mini", update.Description);
    }

    [Fact]
    public void Description_AllFields_ContainsAllSegments()
    {
        var update = new ModelConfigUpdate("orch", "comp", new Dictionary<string, string> { ["reviewer"] = "r-model" }, "mini");
        Assert.Contains("orchestrator→orch", update.Description);
        Assert.Contains("composer→comp", update.Description);
        Assert.Contains("compaction→mini", update.Description);
        Assert.Contains("workers:", update.Description);
    }

    // ── Test 7: Description has no trailing commas or empty segments ────────

    [Fact]
    public void Description_AllNull_IsEmptyString()
    {
        var update = new ModelConfigUpdate(null, null, null, null);
        Assert.Equal("", update.Description);
    }

    [Fact]
    public void Description_SingleField_NoTrailingCommasOrDoubleCommas()
    {
        var update = new ModelConfigUpdate("only-orch", null, null, null);
        var desc = update.Description;
        Assert.DoesNotMatch("^,", desc);
        Assert.DoesNotMatch(",$", desc);
        Assert.DoesNotContain(", ,", desc);
    }
}