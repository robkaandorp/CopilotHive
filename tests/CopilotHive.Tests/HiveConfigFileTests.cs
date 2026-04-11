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

}