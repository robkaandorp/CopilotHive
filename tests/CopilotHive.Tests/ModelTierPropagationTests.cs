using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Verifies per-phase model tier parsing from the Brain's report_iteration_plan tool call.
/// </summary>
public sealed class ModelTierPropagationTests
{
    [Fact]
    public void BuildIterationPlan_WithModelTiers_ParsesPerPhaseTiers()
    {
        var toolCall = MakeToolCall(modelTiers: """{"coding":"premium","review":"premium"}""");
        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Equal(ModelTier.Premium, plan.PhaseTiers[GoalPhase.Coding]);
        Assert.Equal(ModelTier.Premium, plan.PhaseTiers[GoalPhase.Review]);
        Assert.DoesNotContain(GoalPhase.Testing, plan.PhaseTiers.Keys);
    }

    [Fact]
    public void BuildIterationPlan_WithoutModelTiers_PhaseTiersEmpty()
    {
        var toolCall = MakeToolCall(modelTiers: null);
        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Empty(plan.PhaseTiers);
    }

    [Fact]
    public void BuildIterationPlan_StandardTier_ParsesCorrectly()
    {
        var toolCall = MakeToolCall(modelTiers: """{"coding":"standard"}""");
        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Equal(ModelTier.Standard, plan.PhaseTiers[GoalPhase.Coding]);
    }

    [Fact]
    public void BuildIterationPlan_InvalidModelTiersJson_ReturnsEmptyTiers()
    {
        var toolCall = MakeToolCall(modelTiers: "not-valid-json");
        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Empty(plan.PhaseTiers);
    }

    [Theory]
    [InlineData("PREMIUM", ModelTier.Premium)]
    [InlineData("Premium", ModelTier.Premium)]
    [InlineData("STANDARD", ModelTier.Standard)]
    [InlineData("Standard", ModelTier.Standard)]
    [InlineData("unknown-tier", ModelTier.Default)]
    public void BuildIterationPlan_NormalisesModelTierCasing(string tierValue, ModelTier expected)
    {
        var toolCall = MakeToolCall(modelTiers: $$"""{"coding":"{{tierValue}}"}""");
        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Equal(expected, plan.PhaseTiers[GoalPhase.Coding]);
    }

    [Fact]
    public void PhaseTiers_GetValueOrDefault_ReturnsDefaultForUnsetPhases()
    {
        var toolCall = MakeToolCall(modelTiers: """{"coding":"premium"}""");
        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Equal(ModelTier.Premium, plan.PhaseTiers.GetValueOrDefault(GoalPhase.Coding, ModelTier.Default));
        Assert.Equal(ModelTier.Default, plan.PhaseTiers.GetValueOrDefault(GoalPhase.Testing, ModelTier.Default));
    }

    // -- Helpers --

    private static DistributedBrain.IterationPlanResult MakeToolCall(string? modelTiers) =>
        new(
            Phases: ["coding", "testing", "review", "merging"],
            PhaseInstructions: """{"coding":"do the work"}""",
            Reason: "test plan",
            ModelTiers: modelTiers);
}
