using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Verifies the "first non-null wins" model-tier propagation rule across all Brain dispatch methods.
/// </summary>
public sealed class ModelTierPropagationTests
{
    // ── ApplyModelTierIfNotSet unit tests ─────────────────────────────────

    [Fact]
    public void ApplyModelTierIfNotSet_PipelineTierEmpty_SetsToStandard()
    {
        var pipeline = CreatePipeline();

        DistributedBrain.ApplyModelTierIfNotSet(pipeline, "standard");

        Assert.Equal("standard", pipeline.LatestModelTier);
    }

    [Fact]
    public void ApplyModelTierIfNotSet_PipelineTierEmpty_SetsToPremium()
    {
        var pipeline = CreatePipeline();

        DistributedBrain.ApplyModelTierIfNotSet(pipeline, "premium");

        Assert.Equal("premium", pipeline.LatestModelTier);
    }

    [Fact]
    public void ApplyModelTierIfNotSet_PipelineTierEmpty_NullModelTier_DoesNotSet()
    {
        var pipeline = CreatePipeline();

        DistributedBrain.ApplyModelTierIfNotSet(pipeline, null);

        Assert.Equal("", pipeline.LatestModelTier);
    }

    [Fact]
    public void ApplyModelTierIfNotSet_PipelineTierAlreadyPremium_StandardDoesNotOverwrite()
    {
        // This is the core "first non-null wins" scenario:
        // DecideNextStepAsync returns "premium" first; CraftPromptAsync returns "standard" — premium must survive.
        var pipeline = CreatePipeline();
        DistributedBrain.ApplyModelTierIfNotSet(pipeline, "premium");
        Assert.Equal("premium", pipeline.LatestModelTier);

        DistributedBrain.ApplyModelTierIfNotSet(pipeline, "standard");

        Assert.Equal("premium", pipeline.LatestModelTier);
    }

    [Fact]
    public void ApplyModelTierIfNotSet_PipelineTierAlreadyStandard_PremiumDoesNotOverwrite()
    {
        var pipeline = CreatePipeline();
        DistributedBrain.ApplyModelTierIfNotSet(pipeline, "standard");
        Assert.Equal("standard", pipeline.LatestModelTier);

        DistributedBrain.ApplyModelTierIfNotSet(pipeline, "premium");

        Assert.Equal("standard", pipeline.LatestModelTier);
    }

    [Theory]
    [InlineData("PREMIUM", "premium")]
    [InlineData("Premium", "premium")]
    [InlineData("STANDARD", "standard")]
    [InlineData("Standard", "standard")]
    [InlineData("unknown-tier", "standard")]
    public void ApplyModelTierIfNotSet_NormalisesToLowerCaseKnownValues(string input, string expected)
    {
        var pipeline = CreatePipeline();

        DistributedBrain.ApplyModelTierIfNotSet(pipeline, input);

        Assert.Equal(expected, pipeline.LatestModelTier);
    }

    // ── Full dispatch-path scenario tests ────────────────────────────────

    /// <summary>
    /// Scenario: DecideNextStepAsync returns "premium"; CraftPromptAsync (called later) returns "standard".
    /// The first-set value ("premium") must survive.
    /// </summary>
    [Fact]
    public async Task DecideNextStepAsync_SetsPremium_ThenCraftPromptAsync_DoesNotOverwrite()
    {
        var brain = new ModelTierTrackingBrain(decideTier: "premium", craftTier: "standard");
        var pipeline = CreatePipeline();

        await brain.DecideNextStepAsync(pipeline, "some context");

        Assert.Equal("premium", pipeline.LatestModelTier);

        await brain.CraftPromptAsync(pipeline, "coder");

        Assert.Equal("premium", pipeline.LatestModelTier);
    }

    /// <summary>
    /// Scenario: DecideNextStepAsync returns null model_tier; CraftPromptAsync returns "premium".
    /// The tier should be set to "premium" by CraftPromptAsync since nothing set it first.
    /// </summary>
    [Fact]
    public async Task DecideNextStepAsync_ReturnsNullTier_ThenCraftPromptAsync_SetsPremium()
    {
        var brain = new ModelTierTrackingBrain(decideTier: null, craftTier: "premium");
        var pipeline = CreatePipeline();

        await brain.DecideNextStepAsync(pipeline, "some context");

        Assert.Equal("", pipeline.LatestModelTier);

        await brain.CraftPromptAsync(pipeline, "coder");

        Assert.Equal("premium", pipeline.LatestModelTier);
    }

    /// <summary>
    /// Scenario: PlanGoalAsync returns "premium" — pipeline tier should be set.
    /// </summary>
    [Fact]
    public async Task PlanGoalAsync_ReturnsPremiumTier_SetsPipelineTier()
    {
        var brain = new ModelTierTrackingBrain(planTier: "premium");
        var pipeline = CreatePipeline();

        await brain.PlanGoalAsync(pipeline);

        Assert.Equal("premium", pipeline.LatestModelTier);
    }

    /// <summary>
    /// Scenario: InterpretOutputAsync returns "premium" — pipeline tier should be set when empty.
    /// </summary>
    [Fact]
    public async Task InterpretOutputAsync_ReturnsPremiumTier_SetsPipelineTier()
    {
        var brain = new ModelTierTrackingBrain(interpretTier: "premium");
        var pipeline = CreatePipeline();

        await brain.InterpretOutputAsync(pipeline, GoalPhase.Coding, "some output");

        Assert.Equal("premium", pipeline.LatestModelTier);
    }

    /// <summary>
    /// Scenario: PlanGoalAsync sets "premium"; subsequent InterpretOutputAsync returns "standard".
    /// Premium must survive.
    /// </summary>
    [Fact]
    public async Task PlanGoalAsync_SetsPremium_ThenInterpretOutputAsync_DoesNotOverwrite()
    {
        var brain = new ModelTierTrackingBrain(planTier: "premium", interpretTier: "standard");
        var pipeline = CreatePipeline();

        await brain.PlanGoalAsync(pipeline);
        Assert.Equal("premium", pipeline.LatestModelTier);

        await brain.InterpretOutputAsync(pipeline, GoalPhase.Coding, "output");

        Assert.Equal("premium", pipeline.LatestModelTier);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static GoalPipeline CreatePipeline() =>
        new(new Goal { Id = "test-goal", Description = "Test goal" });
}

/// <summary>
/// A brain stub that calls <see cref="DistributedBrain.ApplyModelTierIfNotSet"/> exactly as the
/// real implementation does, allowing tests to verify propagation logic without an LLM connection.
/// </summary>
file sealed class ModelTierTrackingBrain : IDistributedBrain
{
    private readonly string? _planTier;
    private readonly string? _craftTier;
    private readonly string? _interpretTier;
    private readonly string? _decideTier;

    public ModelTierTrackingBrain(
        string? planTier = null,
        string? craftTier = null,
        string? interpretTier = null,
        string? decideTier = null)
    {
        _planTier = planTier;
        _craftTier = craftTier;
        _interpretTier = interpretTier;
        _decideTier = decideTier;
    }

    public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<OrchestratorDecision> PlanGoalAsync(GoalPipeline pipeline, CancellationToken ct = default)
    {
        var decision = new OrchestratorDecision
        {
            Action = OrchestratorActionType.SpawnCoder,
            ModelTier = _planTier,
        };
        DistributedBrain.ApplyModelTierIfNotSet(pipeline, decision.ModelTier);
        return Task.FromResult(decision);
    }

    public Task<IterationPlan> PlanIterationAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
        Task.FromResult(IterationPlan.Default());

    public Task<string> CraftPromptAsync(
        GoalPipeline pipeline, string workerRole, string? additionalContext = null, CancellationToken ct = default)
    {
        var decision = new OrchestratorDecision { ModelTier = _craftTier };
        DistributedBrain.ApplyModelTierIfNotSet(pipeline, decision.ModelTier);
        return Task.FromResult($"Work on {pipeline.Description} as {workerRole}");
    }

    public Task<OrchestratorDecision> InterpretOutputAsync(GoalPipeline pipeline, GoalPhase phase, string workerOutput, CancellationToken ct = default)
    {
        var decision = new OrchestratorDecision
        {
            Action = OrchestratorActionType.Done,
            Verdict = "PASS",
            ModelTier = _interpretTier,
        };
        DistributedBrain.ApplyModelTierIfNotSet(pipeline, decision.ModelTier);
        return Task.FromResult(decision);
    }

    public Task<OrchestratorDecision> DecideNextStepAsync(
        GoalPipeline pipeline, string context, CancellationToken ct = default)
    {
        var decision = new OrchestratorDecision
        {
            Action = OrchestratorActionType.Done,
            ModelTier = _decideTier,
        };
        DistributedBrain.ApplyModelTierIfNotSet(pipeline, decision.ModelTier);
        return Task.FromResult(decision);
    }

    public Task InformAsync(GoalPipeline pipeline, string information, CancellationToken ct = default) =>
        Task.CompletedTask;
}
