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
    public void BuildIterationPlan_WithModelTiers_ParsesPerRoleTiers()
    {
        var toolCall = MakeToolCall(modelTiers: """{"coder":"premium","reviewer":"premium"}""");
        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Equal(ModelTier.Premium, plan.RoleTiers[WorkerRole.Coder]);
        Assert.Equal(ModelTier.Premium, plan.RoleTiers[WorkerRole.Reviewer]);
        Assert.DoesNotContain(WorkerRole.Tester, plan.RoleTiers.Keys);
    }

    [Fact]
    public void BuildIterationPlan_WithoutModelTiers_RoleTiersEmpty()
    {
        var toolCall = MakeToolCall(modelTiers: null);
        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Empty(plan.RoleTiers);
    }

    [Fact]
    public void BuildIterationPlan_StandardTier_ParsesCorrectly()
    {
        var toolCall = MakeToolCall(modelTiers: """{"coder":"standard"}""");
        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Equal(ModelTier.Standard, plan.RoleTiers[WorkerRole.Coder]);
    }

    [Fact]
    public void BuildIterationPlan_InvalidModelTiersJson_ReturnsEmptyTiers()
    {
        var toolCall = MakeToolCall(modelTiers: "not-valid-json");
        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Empty(plan.RoleTiers);
    }

    [Theory]
    [InlineData("PREMIUM", ModelTier.Premium)]
    [InlineData("Premium", ModelTier.Premium)]
    [InlineData("STANDARD", ModelTier.Standard)]
    [InlineData("Standard", ModelTier.Standard)]
    [InlineData("unknown-tier", ModelTier.Default)]
    public void BuildIterationPlan_NormalisesModelTierCasing(string tierValue, ModelTier expected)
    {
        var toolCall = MakeToolCall(modelTiers: $$"""{"coder":"{{tierValue}}"}""");
        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Equal(expected, plan.RoleTiers[WorkerRole.Coder]);
    }

    [Fact]
    public void RoleTiers_GetValueOrDefault_ReturnsDefaultForUnsetRoles()
    {
        var toolCall = MakeToolCall(modelTiers: """{"coder":"premium"}""");
        var plan = DistributedBrain.BuildIterationPlanFromToolCall(toolCall);

        Assert.Equal(ModelTier.Premium, plan.RoleTiers.GetValueOrDefault(WorkerRole.Coder, ModelTier.Default));
        Assert.Equal(ModelTier.Default, plan.RoleTiers.GetValueOrDefault(WorkerRole.Tester, ModelTier.Default));
    }

    // -- Helpers --

    private static DistributedBrain.BrainToolCallResult MakeToolCall(string? modelTiers) =>
        new("report_iteration_plan", new Dictionary<string, object?>
        {
            ["phases"] = new[] { "coding", "testing", "review", "merging" },
            ["phase_instructions"] = """{"coding":"do the work"}""",
            ["reason"] = "test plan",
            ["model_tiers"] = modelTiers,
        });
}
