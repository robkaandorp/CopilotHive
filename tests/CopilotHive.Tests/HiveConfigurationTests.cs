using CopilotHive.Configuration;
using CopilotHive.Workers;

namespace CopilotHive.Tests;

public class HiveConfigurationTests
{
    [Fact]
    public void GetModelForRole_Defaults_ReturnsExpectedModels()
    {
        var config = new HiveConfiguration
        {
            Goal = "test",
            GitHubToken = "fake",
        };

        Assert.Equal("claude-opus-4.6", config.GetModelForRole(WorkerRole.Coder));
        Assert.Equal("gpt-5.3-codex", config.GetModelForRole(WorkerRole.Reviewer));
        Assert.Equal("claude-sonnet-4.6", config.GetModelForRole(WorkerRole.Tester));
        Assert.Equal("claude-sonnet-4.6", config.GetModelForRole(WorkerRole.Improver));
    }

    [Fact]
    public void GetModelForRole_WithOverrides_ReturnsOverriddenModels()
    {
        var config = new HiveConfiguration
        {
            Goal = "test",
            GitHubToken = "fake",
            CoderModel = "gpt-5.4",
            ReviewerModel = "claude-opus-4.6",
            TesterModel = "gpt-5-mini",
            ImproverModel = "gpt-5.2",
        };

        Assert.Equal("gpt-5.4", config.GetModelForRole(WorkerRole.Coder));
        Assert.Equal("claude-opus-4.6", config.GetModelForRole(WorkerRole.Reviewer));
        Assert.Equal("gpt-5-mini", config.GetModelForRole(WorkerRole.Tester));
        Assert.Equal("gpt-5.2", config.GetModelForRole(WorkerRole.Improver));
    }

    [Fact]
    public void GetModelForRole_FallbackModel_UsedForCoderAndUnmatchedRoles()
    {
        var config = new HiveConfiguration
        {
            Goal = "test",
            GitHubToken = "fake",
            Model = "claude-sonnet-4.6",
        };

        // Coder falls back to Model (no CoderModel set)
        Assert.Equal("claude-sonnet-4.6", config.GetModelForRole(WorkerRole.Coder));
        // MergeWorker falls to default _ => Model
        Assert.Equal("claude-sonnet-4.6", config.GetModelForRole(WorkerRole.MergeWorker));
    }

    [Fact]
    public void GetModelForRole_EnumValues_ReturnExpectedDefaults()
    {
        var config = new HiveConfiguration
        {
            Goal = "test",
            GitHubToken = "fake",
        };

        Assert.Equal("claude-opus-4.6", config.GetModelForRole(WorkerRole.Coder));
        Assert.Equal("gpt-5.3-codex", config.GetModelForRole(WorkerRole.Reviewer));
        Assert.Equal("claude-sonnet-4.6", config.GetModelForRole(WorkerRole.Tester));
    }

    [Fact]
    public void GetModelForRole_Orchestrator_DefaultsSonnet()
    {
        var config = new HiveConfiguration
        {
            Goal = "test",
            GitHubToken = "fake",
        };

        Assert.Equal("claude-sonnet-4.6", config.GetModelForRole(WorkerRole.Orchestrator));
    }

    [Fact]
    public void GetModelForRole_Orchestrator_WithOverride()
    {
        var config = new HiveConfiguration
        {
            Goal = "test",
            GitHubToken = "fake",
            OrchestratorModel = "gpt-5-mini",
        };

        Assert.Equal("gpt-5-mini", config.GetModelForRole(WorkerRole.Orchestrator));
    }
}
