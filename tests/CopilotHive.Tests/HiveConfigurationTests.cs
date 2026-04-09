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
            Orchestrator = { WorkerContextWindow = 100_000 },
            Workers =
            {
                ["coder"] = new WorkerConfig { ContextWindow = coderWindow },
            },
        };

        Assert.Equal(coderWindow, config.GetContextWindowForRole(WorkerRole.Coder));
    }

    [Fact]
    public void GetContextWindowForRole_NoPerRole_FallsBackToOrchestratorWorkerContextWindow()
    {
        const int orchestratorWindow = 120_000;
        var config = new HiveConfigFile
        {
            Orchestrator = { WorkerContextWindow = orchestratorWindow },
        };

        Assert.Equal(orchestratorWindow, config.GetContextWindowForRole(WorkerRole.Tester));
    }

    [Fact]
    public void GetContextWindowForRole_NothingConfigured_ReturnsDefaultBrainContextWindow()
    {
        var config = new HiveConfigFile();

        Assert.Equal(Constants.DefaultBrainContextWindow, config.GetContextWindowForRole(WorkerRole.Coder));
    }
}
