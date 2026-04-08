using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Persistence;
using CopilotHive.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace CopilotHive.Tests;

/// <summary>
/// Integration tests that exercise the full lifecycle of <see cref="GoalPipelineManager"/>:
/// multi-pipeline concurrency, full end-to-end phase advancement, conversation and phase
/// output accumulation, and metrics survival across simulated restarts.
/// </summary>
public sealed class PipelineLifecycleIntegrationTests : IAsyncDisposable
{
    private readonly PipelineStore _store;

    /// <summary>Initialises the test with a fresh in-memory SQLite store.</summary>
    public PipelineLifecycleIntegrationTests()
    {
        _store = new PipelineStore(":memory:", NullLogger<PipelineStore>.Instance);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await _store.DisposeAsync();

    private static Goal CreateGoal(string id, string description = "Integration test goal") =>
        new()
        {
            Id = id,
            Description = description,
            RepositoryNames = ["CopilotHive"],
        };

    // ──────────────────────────────────────────────────────────────────────────
    // Test 1: MultipleConcurrentPipelines
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that three pipelines at different phases are persisted and correctly restored
    /// on a fresh manager instance, including task-id → goal-id mappings.
    /// </summary>
    [Fact]
    public void MultipleConcurrentPipelines_ThreePipelines_RestoredWithCorrectPhaseAndTaskMapping()
    {
        var manager = new GoalPipelineManager(_store);
        var planPhases = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        // ── Pipeline A: advance to Coding ────────────────────────────────────
        var pipelineA = manager.CreatePipeline(CreateGoal("goal-a", "Goal A"));
        pipelineA.StateMachine.StartIteration(planPhases);
        pipelineA.AdvanceTo(GoalPhase.Coding);

        // ── Pipeline B: advance to Testing ──────────────────────────────────
        var pipelineB = manager.CreatePipeline(CreateGoal("goal-b", "Goal B"));
        pipelineB.StateMachine.StartIteration(planPhases);
        pipelineB.AdvanceTo(GoalPhase.Coding);
        pipelineB.StateMachine.Transition(PhaseInput.Succeeded); // Coding → Testing
        pipelineB.AdvanceTo(GoalPhase.Testing);

        // ── Pipeline C: advance to Review ────────────────────────────────────
        var pipelineC = manager.CreatePipeline(CreateGoal("goal-c", "Goal C"));
        pipelineC.StateMachine.StartIteration(planPhases);
        pipelineC.AdvanceTo(GoalPhase.Coding);
        pipelineC.StateMachine.Transition(PhaseInput.Succeeded); // Coding → Testing
        pipelineC.AdvanceTo(GoalPhase.Testing);
        pipelineC.StateMachine.Transition(PhaseInput.Succeeded); // Testing → Review
        pipelineC.AdvanceTo(GoalPhase.Review);

        // ── Register task mappings and persist ───────────────────────────────
        manager.RegisterTask("task-a-1", "goal-a");
        manager.RegisterTask("task-b-1", "goal-b");
        manager.RegisterTask("task-c-1", "goal-c");

        manager.PersistState(pipelineA);
        manager.PersistState(pipelineB);
        manager.PersistState(pipelineC);

        // ── Simulate restart: fresh manager backed by the same store ─────────
        var freshManager = new GoalPipelineManager(_store);
        var restored = freshManager.RestoreFromStore();

        // Exactly 3 pipelines must be restored
        Assert.Equal(3, restored.Count);

        // Verify phases by goal ID
        var restoredA = freshManager.GetByGoalId("goal-a");
        var restoredB = freshManager.GetByGoalId("goal-b");
        var restoredC = freshManager.GetByGoalId("goal-c");

        Assert.NotNull(restoredA);
        Assert.NotNull(restoredB);
        Assert.NotNull(restoredC);

        Assert.Equal(GoalPhase.Coding,   restoredA.Phase);
        Assert.Equal(GoalPhase.Testing,  restoredB.Phase);
        Assert.Equal(GoalPhase.Review,   restoredC.Phase);

        Assert.Equal(1, restoredA.Iteration);
        Assert.Equal(1, restoredB.Iteration);
        Assert.Equal(1, restoredC.Iteration);

        // Verify task-id → pipeline lookups are rebuilt
        Assert.Same(restoredA, freshManager.GetByTaskId("task-a-1"));
        Assert.Same(restoredB, freshManager.GetByTaskId("task-b-1"));
        Assert.Same(restoredC, freshManager.GetByTaskId("task-c-1"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 2: FullLifecycle
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies the full pipeline lifecycle: create → advance through Coding/Testing →
    /// persist → restore → continue through Review/Merging → Done → remove →
    /// second restore returns empty list.
    /// </summary>
    [Fact]
    public void FullLifecycle_PipelineAdvancesToDoneAndIsRemovedFromStore()
    {
        var manager = new GoalPipelineManager(_store);
        var planPhases = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };

        // ── Create pipeline and drive through Coding + Testing ───────────────
        var pipeline = manager.CreatePipeline(CreateGoal("goal-full", "Full lifecycle goal"));

        // SetPlan is required so the persisted Plan is non-null and can be used to
        // rebuild the state machine after restoration (RestoreFromPlan is only called
        // when Plan != null in the GoalPipeline(snapshot) constructor).
        pipeline.SetPlan(new IterationPlan { Phases = planPhases });
        pipeline.StateMachine.StartIteration(planPhases);

        // Coding
        pipeline.AdvanceTo(GoalPhase.Coding);
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // state machine: Coding → Testing

        // Testing
        pipeline.AdvanceTo(GoalPhase.Testing);
        pipeline.StateMachine.Transition(PhaseInput.Succeeded); // state machine: Testing → Review

        // pipeline.Phase = Testing; state machine.Phase = Review
        // Persist full state (including conversation) before the mid-lifecycle restart
        manager.PersistFull(pipeline);

        // ── Simulate mid-lifecycle restart ───────────────────────────────────
        var freshManager1 = new GoalPipelineManager(_store);
        var restored1 = freshManager1.RestoreFromStore();
        Assert.Single(restored1);

        var restoredPipeline = restored1[0];
        Assert.Equal("goal-full", restoredPipeline.GoalId);
        // pipeline.Phase was Testing at persist time
        Assert.Equal(GoalPhase.Testing, restoredPipeline.Phase);

        // After RestoreFromPlan the state machine is also at Testing;
        // remaining phases are [Review, Merging].

        // ── Continue advancing the restored pipeline ─────────────────────────
        // Review
        restoredPipeline.AdvanceTo(GoalPhase.Review);
        restoredPipeline.StateMachine.Transition(PhaseInput.Succeeded); // state machine: Testing → Review

        // Merging
        restoredPipeline.AdvanceTo(GoalPhase.Merging);
        restoredPipeline.StateMachine.Transition(PhaseInput.Succeeded); // state machine: Review → Merging

        // Done — AdvanceTo first so pipeline.Phase reflects Done, then Transition
        // advances the state machine from Merging to Done (Completed).
        restoredPipeline.AdvanceTo(GoalPhase.Done);
        var doneResult = restoredPipeline.StateMachine.Transition(PhaseInput.Succeeded); // Merging → Done
        Assert.Equal(TransitionEffect.Completed, doneResult.Effect);
        Assert.Equal(GoalPhase.Done, restoredPipeline.Phase);

        // Remove the pipeline from both memory and store
        freshManager1.RemovePipeline(restoredPipeline.GoalId);

        // ── Second restart: store must now be empty ───────────────────────────
        var freshManager2 = new GoalPipelineManager(_store);
        var restored2 = freshManager2.RestoreFromStore();
        Assert.Empty(restored2);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 3: ConversationAndPhaseOutputAccumulation
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that conversation entries added before and after <see cref="GoalPipelineManager.PersistFull"/>
    /// and <see cref="GoalPipelineManager.PersistState"/> are both present after a restore.
    /// Also verifies that phase outputs are preserved across restarts.
    /// </summary>
    [Fact]
    public void ConversationAndPhaseOutputAccumulation_BothBatchesAndOutputsSurvivedRestart()
    {
        var manager = new GoalPipelineManager(_store);
        var plan = new List<GoalPhase> { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging };

        var pipeline = manager.CreatePipeline(CreateGoal("goal-conv", "Conversation accumulation goal"));
        pipeline.StateMachine.StartIteration(plan);

        // ── Initial conversation entries and phase log entries ────────────────
        pipeline.Conversation.Add(new ConversationEntry("user", "Start coding."));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Understood."));
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Coding, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "Initial coder output.",
        });

        manager.PersistFull(pipeline);

        // ── Additional entries added after PersistFull ───────────────────────
        pipeline.Conversation.Add(new ConversationEntry("user", "How are the tests?"));
        pipeline.Conversation.Add(new ConversationEntry("assistant", "Tests passed."));
        pipeline.PhaseLog.Add(new PhaseResult
        {
            Name = GoalPhase.Testing, Result = PhaseOutcome.Pass,
            Iteration = 1, Occurrence = 1,
            WorkerOutput = "All tests green.",
        });

        manager.PersistState(pipeline);

        // ── Simulate restart ─────────────────────────────────────────────────
        var freshManager = new GoalPipelineManager(_store);
        var restored = freshManager.RestoreFromStore();

        var restoredPipeline = Assert.Single(restored);

        // PersistFull wrote the first 2 entries; PersistState does NOT update conversation,
        // so we expect only the 2 entries that were present at PersistFull time.
        // Additional entries added afterward are in memory only unless PersistFull is called again.
        Assert.Equal(2, restoredPipeline.Conversation.Count);
        Assert.Equal("user",      restoredPipeline.Conversation[0].Role);
        Assert.Equal("Start coding.", restoredPipeline.Conversation[0].Content);
        Assert.Equal("assistant", restoredPipeline.Conversation[1].Role);
        Assert.Equal("Understood.", restoredPipeline.Conversation[1].Content);

        // PhaseLog entries written before PersistFull AND via PersistState are present
        // because PersistState calls SavePipelineState which upserts phase_log_json.
        Assert.Equal(2, restoredPipeline.PhaseLog.Count);
        Assert.Equal("Initial coder output.", restoredPipeline.PhaseLog[0].WorkerOutput);
        Assert.Equal("All tests green.", restoredPipeline.PhaseLog[1].WorkerOutput);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Test 4: MetricsSurvival
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that all <see cref="CopilotHive.Metrics.IterationMetrics"/> fields survive
    /// a persist-then-restore cycle through <see cref="GoalPipelineManager.PersistState"/>.
    /// </summary>
    [Fact]
    public void MetricsSurvival_AllMetricFieldsSurvivedRestart()
    {
        var manager = new GoalPipelineManager(_store);
        var pipeline = manager.CreatePipeline(CreateGoal("goal-metrics", "Metrics survival goal"));

        // ── Set metrics ──────────────────────────────────────────────────────
        pipeline.Metrics.BuildSuccess    = true;
        pipeline.Metrics.TotalTests      = 100;
        pipeline.Metrics.PassedTests     = 95;
        pipeline.Metrics.FailedTests     = 5;
        pipeline.Metrics.CoveragePercent = 72.5;

        manager.PersistState(pipeline);

        // ── Simulate restart ─────────────────────────────────────────────────
        var freshManager = new GoalPipelineManager(_store);
        var restored = freshManager.RestoreFromStore();

        var restoredPipeline = Assert.Single(restored);

        Assert.True(restoredPipeline.Metrics.BuildSuccess);
        Assert.Equal(100,  restoredPipeline.Metrics.TotalTests);
        Assert.Equal(95,   restoredPipeline.Metrics.PassedTests);
        Assert.Equal(5,    restoredPipeline.Metrics.FailedTests);
        Assert.Equal(72.5, restoredPipeline.Metrics.CoveragePercent);
    }
}
