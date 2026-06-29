using CopilotHive.Configuration;
using CopilotHive.Workers;

namespace CopilotHive.Tests;

public class HiveConfigurationTests
{
    [Fact]
    public void GetModelForRole_PerRoleOverride_ReturnsConfiguredModel()
    {
        var config = new HiveConfigFile
        {
            Workers =
            {
                ["coder"] = new WorkerConfig { Model = "gpt-5.4" },
                ["reviewer"] = new WorkerConfig { Model = "claude-opus-4.6" },
                ["tester"] = new WorkerConfig { Model = "gpt-5-mini" },
                ["improver"] = new WorkerConfig { Model = "gpt-5.2" },
            },
        };

        Assert.Equal("gpt-5.4", config.GetModelForRole(WorkerRole.Coder));
        Assert.Equal("claude-opus-4.6", config.GetModelForRole(WorkerRole.Reviewer));
        Assert.Equal("gpt-5-mini", config.GetModelForRole(WorkerRole.Tester));
        Assert.Equal("gpt-5.2", config.GetModelForRole(WorkerRole.Improver));
    }

    [Fact]
    public void GetModelForRole_NoPerRoleConfig_FallsBackToOrchestratorModel()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = { Model = "custom-fallback-model" },
        };

        Assert.Equal("custom-fallback-model", config.GetModelForRole(WorkerRole.Coder));
        Assert.Equal("custom-fallback-model", config.GetModelForRole(WorkerRole.Reviewer));
        Assert.Equal("custom-fallback-model", config.GetModelForRole(WorkerRole.Tester));
        Assert.Equal("custom-fallback-model", config.GetModelForRole(WorkerRole.Improver));
        Assert.Equal("custom-fallback-model", config.GetModelForRole(WorkerRole.MergeWorker));
    }

    [Fact]
    public void GetModelForRole_NothingConfigured_ReturnsDefaultWorkerModel()
    {
        var config = new HiveConfigFile();

        // Orchestrator.Model defaults to Constants.DefaultWorkerModel
        Assert.Equal(Constants.DefaultWorkerModel, config.GetModelForRole(WorkerRole.Coder));
        Assert.Equal(Constants.DefaultWorkerModel, config.GetModelForRole(WorkerRole.Reviewer));
        Assert.Equal(Constants.DefaultWorkerModel, config.GetModelForRole(WorkerRole.Tester));
        Assert.Equal(Constants.DefaultWorkerModel, config.GetModelForRole(WorkerRole.DocWriter));
    }

    [Fact]
    public void GetModelForRole_MultipleRolesConfigured_EachReturnsItsOwnModel()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = { Model = "fallback-model" },
            Workers =
            {
                ["coder"] = new WorkerConfig { Model = "coder-specific-model" },
                ["docwriter"] = new WorkerConfig { Model = "docwriter-specific-model" },
            },
        };

        Assert.Equal("coder-specific-model", config.GetModelForRole(WorkerRole.Coder));
        Assert.Equal("docwriter-specific-model", config.GetModelForRole(WorkerRole.DocWriter));
        // Unconfigured roles fall back to Orchestrator.Model
        Assert.Equal("fallback-model", config.GetModelForRole(WorkerRole.Tester));
        Assert.Equal("fallback-model", config.GetModelForRole(WorkerRole.Reviewer));
        Assert.Equal("fallback-model", config.GetModelForRole(WorkerRole.Improver));
    }

    [Fact]
    public void GetContextWindowForRole_PerRoleSet_ReturnsPerRoleValue()
    {
        const int coderWindow = 200_000;
        var config = new HiveConfigFile
        {
            Workers =
            {
                ["coder"] = new WorkerConfig { ContextWindow = coderWindow },
            },
        };

        Assert.Equal(coderWindow, config.GetContextWindowForRole(WorkerRole.Coder));
    }

    [Fact]
    public void GetContextWindowForRole_NoPerRole_FallsBackToModelSpecificContextWindow()
    {
        const int modelWindow = 120_000;
        var config = new HiveConfigFile
        {
            Orchestrator = { Model = "test-model" },
            Models = new ModelsConfig { AvailableModels = [new ModelEntry { Name = "test-model", ContextWindow = modelWindow }] }
        };

        Assert.Equal(modelWindow, config.GetContextWindowForRole(WorkerRole.Tester));
    }

    [Fact]
    public void GetContextWindowForRole_NothingConfigured_ReturnsDefaultBrainContextWindow()
    {
        var config = new HiveConfigFile();

        Assert.Equal(Constants.DefaultBrainContextWindow, config.GetContextWindowForRole(WorkerRole.Coder));
    }

    // ── Integration tests for simplified context window resolution chains ────────

    [Fact]
    public void GetContextWindowForRole_PerRoleOverrideWins_WhenModelSpecificAlsoSet()
    {
        const int perRoleWindow = 250_000;
        const int modelWindow = 120_000;
        var config = new HiveConfigFile
        {
            Orchestrator = { Model = "coder-model" },
            Workers =
            {
                ["coder"] = new WorkerConfig { ContextWindow = perRoleWindow, Model = "coder-model" },
            },
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "coder-model", ContextWindow = modelWindow }]
            }
        };

        // Per-role override should win over model-specific
        Assert.Equal(perRoleWindow, config.GetContextWindowForRole(WorkerRole.Coder));
    }

    [Fact]
    public void GetContextWindowForRole_ModelSpecificFallback_WhenNoPerRoleSet()
    {
        const int modelWindow = 180_000;
        var config = new HiveConfigFile
        {
            Orchestrator = { Model = "fallback-model" },
            Workers =
            {
                // No ContextWindow set on the worker — only Model
                ["tester"] = new WorkerConfig { Model = "tester-specific-model" },
            },
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "tester-specific-model", ContextWindow = modelWindow }]
            }
        };

        // No per-role ContextWindow, but model-specific should be returned
        Assert.Equal(modelWindow, config.GetContextWindowForRole(WorkerRole.Tester));
    }

    [Fact]
    public void GetContextWindowForRole_DefaultBrainContextWindow_WhenNothingConfigured()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = { Model = "unknown-model" },
            // No per-role workers, no AvailableModels — should fall back to DefaultBrainContextWindow
        };

        Assert.Equal(Constants.DefaultBrainContextWindow, config.GetContextWindowForRole(WorkerRole.Coder));
        Assert.Equal(Constants.DefaultBrainContextWindow, config.GetContextWindowForRole(WorkerRole.Tester));
        Assert.Equal(Constants.DefaultBrainContextWindow, config.GetContextWindowForRole(WorkerRole.Reviewer));
    }

    [Fact]
    public void GetContextWindowForRole_ModelNotInAvailableModels_FallsBackToDefault()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = { Model = "orchestrator-model" },
            Models = new ModelsConfig
            {
                AvailableModels =
                [
                    new ModelEntry { Name = "other-model", ContextWindow = 100_000 }
                ]
            }
        };

        // The orchestrator-model is not in AvailableModels, so no model-specific value found
        Assert.Equal(Constants.DefaultBrainContextWindow, config.GetContextWindowForRole(WorkerRole.Coder));
    }

    [Fact]
    public void GetContextWindowForRole_PerRoleZero_FallsThroughToModelSpecific()
    {
        const int modelWindow = 150_000;
        var config = new HiveConfigFile
        {
            Orchestrator = { Model = "test-model" },
            Workers =
            {
                // ContextWindow of 0 means "not set" — should fall through
                ["coder"] = new WorkerConfig { ContextWindow = 0, Model = "test-model" },
            },
            Models = new ModelsConfig
            {
                AvailableModels = [new ModelEntry { Name = "test-model", ContextWindow = modelWindow }]
            }
        };

        // Per-role is 0 (not set), should fall through to model-specific
        Assert.Equal(modelWindow, config.GetContextWindowForRole(WorkerRole.Coder));
    }
}
