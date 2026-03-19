using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Verifies the "first non-null wins" model-tier propagation rule across all Brain dispatch methods.
/// </summary>
public sealed class ModelTierPropagationTests
{
    // -- ApplyModelTierIfNotSet unit tests --

    [Fact]
    public void ApplyModelTierIfNotSet_PipelineTierEmpty_SetsToStandard()
    {
        var pipeline = CreatePipeline();
        DistributedBrain.ApplyModelTierIfNotSet(pipeline, "standard");
        Assert.Equal(ModelTier.Standard, pipeline.LatestModelTier);
    }

    [Fact]
    public void ApplyModelTierIfNotSet_PipelineTierEmpty_SetsToPremium()
    {
        var pipeline = CreatePipeline();
        DistributedBrain.ApplyModelTierIfNotSet(pipeline, "premium");
        Assert.Equal(ModelTier.Premium, pipeline.LatestModelTier);
    }

    [Fact]
    public void ApplyModelTierIfNotSet_PipelineTierEmpty_NullModelTier_DoesNotSet()
    {
        var pipeline = CreatePipeline();
        DistributedBrain.ApplyModelTierIfNotSet(pipeline, null);
        Assert.Equal(ModelTier.Default, pipeline.LatestModelTier);
    }

    [Fact]
    public void ApplyModelTierIfNotSet_PipelineTierAlreadyPremium_StandardDoesNotOverwrite()
    {
        var pipeline = CreatePipeline();
        DistributedBrain.ApplyModelTierIfNotSet(pipeline, "premium");
        Assert.Equal(ModelTier.Premium, pipeline.LatestModelTier);

        DistributedBrain.ApplyModelTierIfNotSet(pipeline, "standard");
        Assert.Equal(ModelTier.Premium, pipeline.LatestModelTier);
    }

    [Fact]
    public void ApplyModelTierIfNotSet_PipelineTierAlreadyStandard_PremiumDoesNotOverwrite()
    {
        var pipeline = CreatePipeline();
        DistributedBrain.ApplyModelTierIfNotSet(pipeline, "standard");
        Assert.Equal(ModelTier.Standard, pipeline.LatestModelTier);

        DistributedBrain.ApplyModelTierIfNotSet(pipeline, "premium");
        Assert.Equal(ModelTier.Standard, pipeline.LatestModelTier);
    }

    [Theory]
    [InlineData("PREMIUM", ModelTier.Premium)]
    [InlineData("Premium", ModelTier.Premium)]
    [InlineData("STANDARD", ModelTier.Standard)]
    [InlineData("Standard", ModelTier.Standard)]
    [InlineData("unknown-tier", ModelTier.Default)]
    public void ApplyModelTierIfNotSet_NormalisesToLowerCaseKnownValues(string input, ModelTier expected)
    {
        var pipeline = CreatePipeline();
        DistributedBrain.ApplyModelTierIfNotSet(pipeline, input);
        Assert.Equal(expected, pipeline.LatestModelTier);
    }

    // -- Helpers --

    private static GoalPipeline CreatePipeline() =>
        new(new Goal { Id = "test-goal", Description = "Test goal" });
}
