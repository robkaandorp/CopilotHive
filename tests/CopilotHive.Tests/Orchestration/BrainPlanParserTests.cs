using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;

using Xunit;

namespace CopilotHive.Tests.Orchestration;

public class BrainPlanParserTests
{
    [Fact]
    public void MapIterationPlan_MultiRoundPlan_MapsAllPhases()
    {
        var dto = new BrainPlanParser.IterationPlanDto
        {
            Phases = ["coding-1", "testing-1", "coding-2", "testing-2", "review", "merging"],
            Reason = "multi-round",
        };

        var plan = BrainPlanParser.MapIterationPlan(dto);

        Assert.Equal([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging], plan.Phases);
        Assert.Equal("multi-round", plan.Reason);
    }

    [Fact]
    public void MapIterationPlan_SuffixedTierKeys_MapsToBasePhase()
    {
        var dto = new BrainPlanParser.IterationPlanDto
        {
            Phases = ["coding-1"],
            ModelTiers = new Dictionary<string, string> { ["coding-1"] = "premium" },
        };

        var plan = BrainPlanParser.MapIterationPlan(dto);

        Assert.Single(plan.PhaseTiers);
        Assert.Equal(ModelTier.Premium, plan.PhaseTiers[GoalPhase.Coding]);
    }

    [Fact]
    public void MapIterationPlan_DuplicateBaseTierKeys_LastWins()
    {
        var dto = new BrainPlanParser.IterationPlanDto
        {
            Phases = ["coding-1", "coding-2"],
            ModelTiers = new Dictionary<string, string>
            {
                ["coding-1"] = "premium",
                ["coding-2"] = "standard",
            },
        };

        var plan = BrainPlanParser.MapIterationPlan(dto);

        Assert.Single(plan.PhaseTiers);
        Assert.Equal(ModelTier.Standard, plan.PhaseTiers[GoalPhase.Coding]);
    }

    [Fact]
    public void MapIterationPlan_EmptyPhases_ReturnsEmptyPhases()
    {
        var dto = new BrainPlanParser.IterationPlanDto
        {
            Phases = [],
            ModelTiers = null,
        };

        var plan = BrainPlanParser.MapIterationPlan(dto);

        Assert.Empty(plan.Phases);
        Assert.Empty(plan.PhaseTiers);
        Assert.Empty(plan.PhaseInstructions);
        Assert.Null(plan.Reason);
    }

    [Fact]
    public void MapIterationPlan_NullModelTiers_ReturnsEmptyTiers()
    {
        var dto = new BrainPlanParser.IterationPlanDto
        {
            Phases = ["coding"],
            ModelTiers = null,
        };

        var plan = BrainPlanParser.MapIterationPlan(dto);

        Assert.Single(plan.Phases);
        Assert.Empty(plan.PhaseTiers);
    }
}
