using CopilotHive.Configuration;

namespace CopilotHive.Tests;

/// <summary>
/// Unit tests for the Brain STARTUP CONTRACT primitive
/// <see cref="Program.RequireBrainModel(HiveConfigFile)"/>: CopilotHive has no no-Brain operating
/// mode, so an unset (null/empty/whitespace) <c>orchestrator.model</c> fails with the ONE fixed,
/// actionable <see cref="Program.BrainModelRequiredMessage"/>, and a configured model is returned
/// TRIMMED.
/// </summary>
public sealed class RequireBrainModelTests
{
    [Fact]
    public void RequireBrainModel_NullModel_ThrowsWithFixedMessage()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = null }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => Program.RequireBrainModel(config));

        Assert.Equal(Program.BrainModelRequiredMessage, ex.Message);
    }

    [Fact]
    public void RequireBrainModel_EmptyModel_ThrowsWithFixedMessage()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = "" }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => Program.RequireBrainModel(config));

        Assert.Equal(Program.BrainModelRequiredMessage, ex.Message);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    [InlineData(" \t \n ")]
    public void RequireBrainModel_WhitespaceModel_ThrowsWithFixedMessage(string model)
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = model }
        };

        var ex = Assert.Throws<InvalidOperationException>(() => Program.RequireBrainModel(config));

        Assert.Equal(Program.BrainModelRequiredMessage, ex.Message);
    }

    /// <summary>
    /// The message is the SINGLE actionable contract text: it names the missing setting, states
    /// that a Brain is required, and tells the operator how to supply one — with no config values
    /// and no URLs (so it is always safe to surface verbatim).
    /// </summary>
    [Fact]
    public void BrainModelRequiredMessage_IsActionableAndCarriesNoConfigValues()
    {
        Assert.Contains("orchestrator.model", Program.BrainModelRequiredMessage, StringComparison.Ordinal);
        Assert.Contains("requires a Brain", Program.BrainModelRequiredMessage, StringComparison.Ordinal);
        Assert.Contains("--config-repo", Program.BrainModelRequiredMessage, StringComparison.Ordinal);
        Assert.Contains("hive-config.yaml", Program.BrainModelRequiredMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("://", Program.BrainModelRequiredMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("BRAIN_MODEL", Program.BrainModelRequiredMessage, StringComparison.Ordinal);
    }

    /// <summary>
    /// A configured model is returned TRIMMED — the effective model downstream (registration log,
    /// DistributedBrain construction) is the trimmed value, never the raw padded setting.
    /// </summary>
    [Fact]
    public void RequireBrainModel_ConfiguredModel_ReturnsTrimmedValue()
    {
        var config = new HiveConfigFile
        {
            Orchestrator = new OrchestratorConfig { Model = " m " }
        };

        var model = Program.RequireBrainModel(config);

        Assert.Equal("m", model);
    }
}
