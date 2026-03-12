using CopilotHive.Configuration;

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

        Assert.Equal("claude-opus-4.6", config.GetModelForRole("coder"));
        Assert.Equal("gpt-5.3-codex", config.GetModelForRole("reviewer"));
        Assert.Equal("claude-sonnet-4.6", config.GetModelForRole("tester"));
        Assert.Equal("claude-sonnet-4.6", config.GetModelForRole("improver"));
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

        Assert.Equal("gpt-5.4", config.GetModelForRole("coder"));
        Assert.Equal("claude-opus-4.6", config.GetModelForRole("reviewer"));
        Assert.Equal("gpt-5-mini", config.GetModelForRole("tester"));
        Assert.Equal("gpt-5.2", config.GetModelForRole("improver"));
    }

    [Fact]
    public void GetModelForRole_FallbackModel_UsedForCoderAndUnknownRoles()
    {
        var config = new HiveConfiguration
        {
            Goal = "test",
            GitHubToken = "fake",
            Model = "claude-sonnet-4.6",
        };

        // Coder falls back to Model (no CoderModel set)
        Assert.Equal("claude-sonnet-4.6", config.GetModelForRole("coder"));
        // Unknown role falls back to Model
        Assert.Equal("claude-sonnet-4.6", config.GetModelForRole("planner"));
    }

    [Fact]
    public void GetModelForRole_CaseInsensitive()
    {
        var config = new HiveConfiguration
        {
            Goal = "test",
            GitHubToken = "fake",
        };

        Assert.Equal("claude-opus-4.6", config.GetModelForRole("Coder"));
        Assert.Equal("gpt-5.3-codex", config.GetModelForRole("REVIEWER"));
        Assert.Equal("claude-sonnet-4.6", config.GetModelForRole("Tester"));
    }
}
