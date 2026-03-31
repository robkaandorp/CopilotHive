using CopilotHive.Orchestration;
using CopilotHive.Services;

namespace CopilotHive.Tests;

public class PipelineStateMachineTests
{
    private static readonly List<GoalPhase> DefaultPlan =
        [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.DocWriting, GoalPhase.Review, GoalPhase.Merging];

    private static readonly List<GoalPhase> PlanWithImprove =
        [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.DocWriting, GoalPhase.Review, GoalPhase.Improve, GoalPhase.Merging];

    private PipelineStateMachine CreateStarted(IReadOnlyList<GoalPhase>? plan = null)
    {
        var sm = new PipelineStateMachine();
        sm.StartIteration(plan ?? DefaultPlan);
        return sm;
    }

    // ---- Happy path flows ----

    [Fact]
    public void HappyPath_AllPhasesSucceed_ReachesDone()
    {
        var sm = CreateStarted();
        Assert.Equal(GoalPhase.Coding, sm.Phase);

        var r1 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Testing, r1.NextPhase);
        Assert.Equal(TransitionEffect.Continue, r1.Effect);

        var r2 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.DocWriting, r2.NextPhase);
        Assert.Equal(TransitionEffect.Continue, r2.Effect);

        var r3 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Review, r3.NextPhase);
        Assert.Equal(TransitionEffect.Continue, r3.Effect);

        var r4 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Merging, r4.NextPhase);
        Assert.Equal(TransitionEffect.Continue, r4.Effect);

        var r5 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Done, r5.NextPhase);
        Assert.Equal(TransitionEffect.Completed, r5.Effect);
    }

    [Fact]
    public void HappyPath_WithImprove_AllSucceed()
    {
        var sm = CreateStarted(PlanWithImprove);

        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.Succeeded); // → Improve
        Assert.Equal(GoalPhase.Improve, sm.Phase);

        sm.Transition(PhaseInput.Succeeded); // → Merging
        Assert.Equal(GoalPhase.Merging, sm.Phase);

        var done = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Done, done.NextPhase);
        Assert.Equal(TransitionEffect.Completed, done.Effect);
    }

    // ---- THE BUG: RequestChanges → Coder fix → must still run Testing/Review/Merging ----

    [Fact]
    public void ReviewRequestChanges_ThenCoderFix_MustRunAllRemainingPhases()
    {
        var sm = CreateStarted();

        // Iteration 1: Coding → Testing → DocWriting → Review → REQUEST_CHANGES
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        var rc = sm.Transition(PhaseInput.RequestChanges);
        Assert.Equal(GoalPhase.Coding, rc.NextPhase);
        Assert.Equal(TransitionEffect.NewIteration, rc.Effect);

        // Iteration 2: caller re-plans and starts new iteration
        sm.StartIteration(DefaultPlan);
        Assert.Equal(GoalPhase.Coding, sm.Phase);

        // Coder fixes → MUST go through Testing, DocWriting, Review, Merging
        var t1 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Testing, t1.NextPhase);

        var t2 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.DocWriting, t2.NextPhase);

        var t3 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Review, t3.NextPhase);

        var t4 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Merging, t4.NextPhase);

        var t5 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Done, t5.NextPhase);
        Assert.Equal(TransitionEffect.Completed, t5.Effect);
    }

    // ---- DocWriting RequestChanges ----

    [Fact]
    public void DocWritingRequestChanges_LoopsBackToCoding()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting

        var rc = sm.Transition(PhaseInput.RequestChanges);
        Assert.Equal(GoalPhase.Coding, rc.NextPhase);
        Assert.Equal(TransitionEffect.NewIteration, rc.Effect);
    }

    // ---- Testing failure ----

    [Fact]
    public void TestingFailed_LoopsBackToCoding()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing

        var fail = sm.Transition(PhaseInput.Failed);
        Assert.Equal(GoalPhase.Coding, fail.NextPhase);
        Assert.Equal(TransitionEffect.NewIteration, fail.Effect);
    }

    [Fact]
    public void TestingFailed_ThenNewIteration_RunsFullPipeline()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Failed);    // → Coding (NewIteration)

        sm.StartIteration(DefaultPlan);

        // Full pipeline must run again
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.Succeeded); // → Merging
        var done = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(TransitionEffect.Completed, done.Effect);
    }

    // ---- DocWriting failure ----

    [Fact]
    public void DocWritingFailed_LoopsBackToCoding()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting

        var fail = sm.Transition(PhaseInput.Failed);
        Assert.Equal(GoalPhase.Coding, fail.NextPhase);
        Assert.Equal(TransitionEffect.NewIteration, fail.Effect);
    }

    // ---- Review failure ----

    [Fact]
    public void ReviewFailed_LoopsBackToCoding()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review

        var fail = sm.Transition(PhaseInput.Failed);
        Assert.Equal(GoalPhase.Coding, fail.NextPhase);
        Assert.Equal(TransitionEffect.NewIteration, fail.Effect);
    }

    // ---- Merge conflict ----

    [Fact]
    public void MergingFailed_LoopsBackToCoding()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.Succeeded); // → Merging

        var fail = sm.Transition(PhaseInput.Failed);
        Assert.Equal(GoalPhase.Coding, fail.NextPhase);
        Assert.Equal(TransitionEffect.NewIteration, fail.Effect);
    }

    [Fact]
    public void MergeConflict_ThenNewIteration_RunsFullPipeline()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.Succeeded); // → Merging
        sm.Transition(PhaseInput.Failed);    // → Coding (NewIteration)

        sm.StartIteration(DefaultPlan);

        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.Succeeded); // → Merging
        var done = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(TransitionEffect.Completed, done.Effect);
    }

    // ---- Improve non-blocking ----

    [Fact]
    public void ImproveFailed_IsNonBlocking_AdvancesToMerging()
    {
        var sm = CreateStarted(PlanWithImprove);
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.Succeeded); // → Improve

        var result = sm.Transition(PhaseInput.Failed);
        Assert.Equal(GoalPhase.Merging, result.NextPhase);
        Assert.Equal(TransitionEffect.Continue, result.Effect);
    }

    // ---- Coding failure ----

    [Fact]
    public void CodingFailed_LoopsBackToCoding_NewIteration()
    {
        var sm = CreateStarted();

        var fail = sm.Transition(PhaseInput.Failed);
        Assert.Equal(GoalPhase.Coding, fail.NextPhase);
        Assert.Equal(TransitionEffect.NewIteration, fail.Effect);
    }

    // ---- Fail() — retry/iteration exhausted ----

    [Fact]
    public void Fail_SetsTerminalState()
    {
        var sm = CreateStarted();
        sm.Fail();

        Assert.Equal(GoalPhase.Failed, sm.Phase);
        Assert.Empty(sm.RemainingPhases);
    }

    [Fact]
    public void Fail_FromAnyActivePhase_Works()
    {
        foreach (var phase in new[] { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging })
        {
            var sm = CreateStarted();
            // Advance to the target phase
            while (sm.Phase != phase && sm.Phase != GoalPhase.Done)
                sm.Transition(PhaseInput.Succeeded);

            sm.Fail();
            Assert.Equal(GoalPhase.Failed, sm.Phase);
        }
    }

    // ---- Invalid transitions ----

    [Fact]
    public void TransitionFromDone_Throws()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.Succeeded); // → Merging
        sm.Transition(PhaseInput.Succeeded); // → Done

        Assert.Throws<InvalidOperationException>(() => sm.Transition(PhaseInput.Succeeded));
    }

    [Fact]
    public void TransitionFromFailed_Throws()
    {
        var sm = CreateStarted();
        sm.Fail();

        Assert.Throws<InvalidOperationException>(() => sm.Transition(PhaseInput.Succeeded));
    }

    [Fact]
    public void TransitionFromPlanning_WithoutStartIteration_Throws()
    {
        var sm = new PipelineStateMachine();
        Assert.Equal(GoalPhase.Planning, sm.Phase);

        Assert.Throws<InvalidOperationException>(() => sm.Transition(PhaseInput.Succeeded));
    }

    [Fact]
    public void RequestChanges_InvalidAtCoding_Throws()
    {
        var sm = CreateStarted();
        Assert.Throws<InvalidOperationException>(() => sm.Transition(PhaseInput.RequestChanges));
    }

    [Fact]
    public void RequestChanges_InvalidAtTesting_Throws()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing
        Assert.Throws<InvalidOperationException>(() => sm.Transition(PhaseInput.RequestChanges));
    }

    [Fact]
    public void RequestChanges_InvalidAtImprove_Throws()
    {
        var sm = CreateStarted(PlanWithImprove);
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.Succeeded); // → Improve
        Assert.Throws<InvalidOperationException>(() => sm.Transition(PhaseInput.RequestChanges));
    }

    [Fact]
    public void RequestChanges_InvalidAtMerging_Throws()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.Succeeded); // → Merging
        Assert.Throws<InvalidOperationException>(() => sm.Transition(PhaseInput.RequestChanges));
    }

    // ---- StartIteration validation ----

    [Fact]
    public void StartIteration_EmptyPlan_Throws()
    {
        var sm = new PipelineStateMachine();
        Assert.Throws<ArgumentException>(() => sm.StartIteration([]));
    }

    [Fact]
    public void StartIteration_NotStartingWithCodingOrDocWriting_Throws()
    {
        var sm = new PipelineStateMachine();
        Assert.Throws<ArgumentException>(() =>
            sm.StartIteration([GoalPhase.Testing, GoalPhase.Merging]));
    }

    [Fact]
    public void StartIteration_StartingWithDocWriting_IsAccepted()
    {
        var sm = new PipelineStateMachine();
        sm.StartIteration([GoalPhase.DocWriting, GoalPhase.Merging]);
        Assert.Equal(GoalPhase.DocWriting, sm.Phase);
    }

    [Fact]
    public void StartIteration_StartingWithCoding_StillAccepted()
    {
        var sm = new PipelineStateMachine();
        sm.StartIteration([GoalPhase.Coding, GoalPhase.Merging]);
        Assert.Equal(GoalPhase.Coding, sm.Phase);
    }

    [Fact]
    public void StartIteration_DocWritingPlan_NotEndingWithMerging_Throws()
    {
        var sm = new PipelineStateMachine();
        Assert.Throws<ArgumentException>(() =>
            sm.StartIteration([GoalPhase.DocWriting, GoalPhase.Review]));
    }

    [Fact]
    public void DocWritingOnlyPlan_FullFlow_Completes()
    {
        var sm = new PipelineStateMachine();
        sm.StartIteration([GoalPhase.DocWriting, GoalPhase.Review, GoalPhase.Merging]);
        Assert.Equal(GoalPhase.DocWriting, sm.Phase);

        var r1 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Review, r1.NextPhase);
        Assert.Equal(TransitionEffect.Continue, r1.Effect);

        var r2 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Merging, r2.NextPhase);
        Assert.Equal(TransitionEffect.Continue, r2.Effect);

        var r3 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Done, r3.NextPhase);
        Assert.Equal(TransitionEffect.Completed, r3.Effect);
    }

    [Fact]
    public void StartIteration_StartingWithReview_Throws()
    {
        var sm = new PipelineStateMachine();
        Assert.Throws<ArgumentException>(() =>
            sm.StartIteration([GoalPhase.Review, GoalPhase.Merging]));
    }

    [Fact]
    public void StartIteration_NotEndingWithMerging_Throws()
    {
        var sm = new PipelineStateMachine();
        Assert.Throws<ArgumentException>(() =>
            sm.StartIteration([GoalPhase.Coding, GoalPhase.Testing]));
    }

    [Fact]
    public void StartIteration_NullPlan_Throws()
    {
        var sm = new PipelineStateMachine();
        Assert.Throws<ArgumentNullException>(() => sm.StartIteration(null!));
    }

    // ---- Minimal plan (Coding → Merging) ----

    [Fact]
    public void MinimalPlan_CodingToMerging()
    {
        var sm = CreateStarted([GoalPhase.Coding, GoalPhase.Merging]);
        Assert.Equal(GoalPhase.Coding, sm.Phase);

        var r1 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Merging, r1.NextPhase);

        var r2 = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Done, r2.NextPhase);
        Assert.Equal(TransitionEffect.Completed, r2.Effect);
    }

    // ---- CompletedPhases tracking ----

    [Fact]
    public void CompletedPhases_TracksCorrectly()
    {
        var sm = CreateStarted();

        Assert.Empty(sm.CompletedPhases);

        sm.Transition(PhaseInput.Succeeded); // Coding → Testing
        Assert.Contains(GoalPhase.Coding, sm.CompletedPhases);
        Assert.Single(sm.CompletedPhases);

        sm.Transition(PhaseInput.Succeeded); // Testing → DocWriting
        Assert.Contains(GoalPhase.Testing, sm.CompletedPhases);
        Assert.Equal(2, sm.CompletedPhases.Count);
    }

    [Fact]
    public void CompletedPhases_ClearedOnNewIteration()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        Assert.Equal(2, sm.CompletedPhases.Count);

        sm.Transition(PhaseInput.Failed); // → Coding (NewIteration)
        Assert.Empty(sm.CompletedPhases);
    }

    [Fact]
    public void CompletedPhases_IncludesMergingOnDone()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.Succeeded); // → Merging
        sm.Transition(PhaseInput.Succeeded); // → Done

        Assert.Contains(GoalPhase.Merging, sm.CompletedPhases);
    }

    // ---- RemainingPhases tracking ----

    [Fact]
    public void RemainingPhases_DecreasesAsWeAdvance()
    {
        var sm = CreateStarted();
        Assert.Equal(4, sm.RemainingPhases.Count); // Testing, DocWriting, Review, Merging

        sm.Transition(PhaseInput.Succeeded); // → Testing
        Assert.Equal(3, sm.RemainingPhases.Count); // DocWriting, Review, Merging

        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        Assert.Equal(2, sm.RemainingPhases.Count); // Review, Merging
    }

    [Fact]
    public void RemainingPhases_EmptyAfterDone()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.Succeeded); // → Merging
        sm.Transition(PhaseInput.Succeeded); // → Done

        Assert.Empty(sm.RemainingPhases);
    }

    [Fact]
    public void RemainingPhases_EmptyAfterNewIteration()
    {
        var sm = CreateStarted();
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Failed);    // → Coding (NewIteration)

        Assert.Empty(sm.RemainingPhases);
    }

    // ---- Multiple iterations with alternating failures ----

    [Fact]
    public void MultipleIterations_AlternatingFailures_EventuallyCompletes()
    {
        var sm = CreateStarted();

        // Iteration 1: fails at Testing
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Failed);    // → Coding (NewIteration)

        // Iteration 2: fails at Review
        sm.StartIteration(DefaultPlan);
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.RequestChanges); // → Coding (NewIteration)

        // Iteration 3: fails at Merging (conflict)
        sm.StartIteration(DefaultPlan);
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.Succeeded); // → Merging
        sm.Transition(PhaseInput.Failed);    // → Coding (NewIteration)

        // Iteration 4: succeeds
        sm.StartIteration(DefaultPlan);
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → DocWriting
        sm.Transition(PhaseInput.Succeeded); // → Review
        sm.Transition(PhaseInput.Succeeded); // → Merging
        var done = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(TransitionEffect.Completed, done.Effect);
        Assert.Equal(GoalPhase.Done, sm.Phase);
    }

    // ---- Plan with subset of phases (Brain omits DocWriting) ----

    [Fact]
    public void SubsetPlan_SkipsDocWriting()
    {
        var plan = new List<GoalPhase>
            { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging };
        var sm = CreateStarted(plan);

        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → Review (skips DocWriting)
        Assert.Equal(GoalPhase.Review, sm.Phase);

        sm.Transition(PhaseInput.Succeeded); // → Merging
        var done = sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(TransitionEffect.Completed, done.Effect);
    }

    // ---- Initial state ----

    [Fact]
    public void InitialState_IsPlanning()
    {
        var sm = new PipelineStateMachine();
        Assert.Equal(GoalPhase.Planning, sm.Phase);
        Assert.Empty(sm.CompletedPhases);
        Assert.Empty(sm.RemainingPhases);
    }

    [Fact]
    public void StartIteration_SetsPhaseToCoding()
    {
        var sm = new PipelineStateMachine();
        sm.StartIteration(DefaultPlan);
        Assert.Equal(GoalPhase.Coding, sm.Phase);
    }

    // ---- Edge case: StartIteration after Done (should work — goal restart) ----

    [Fact]
    public void StartIteration_ResetsAfterPreviousCompletion()
    {
        var sm = CreateStarted([GoalPhase.Coding, GoalPhase.Merging]);
        sm.Transition(PhaseInput.Succeeded); // → Merging
        sm.Transition(PhaseInput.Succeeded); // → Done
        Assert.Equal(GoalPhase.Done, sm.Phase);

        // Should be able to start a new iteration (e.g., goal was requeued)
        sm.StartIteration(DefaultPlan);
        Assert.Equal(GoalPhase.Coding, sm.Phase);
        Assert.Empty(sm.CompletedPhases);
    }

    // ---- DocWriting + Review both support RequestChanges ----

    [Theory]
    [InlineData(GoalPhase.DocWriting)]
    [InlineData(GoalPhase.Review)]
    public void RequestChanges_ValidAtDocWritingAndReview(GoalPhase targetPhase)
    {
        var sm = CreateStarted();
        // Advance to target phase
        while (sm.Phase != targetPhase)
            sm.Transition(PhaseInput.Succeeded);

        var result = sm.Transition(PhaseInput.RequestChanges);
        Assert.Equal(GoalPhase.Coding, result.NextPhase);
        Assert.Equal(TransitionEffect.NewIteration, result.Effect);
    }

    // ---- All active phases support Failed → NewIteration (except Improve which is non-blocking) ----

    [Theory]
    [InlineData(GoalPhase.Coding)]
    [InlineData(GoalPhase.Testing)]
    [InlineData(GoalPhase.DocWriting)]
    [InlineData(GoalPhase.Review)]
    [InlineData(GoalPhase.Merging)]
    public void Failed_AtAnyActivePhase_CausesNewIteration(GoalPhase targetPhase)
    {
        var sm = CreateStarted();
        while (sm.Phase != targetPhase)
            sm.Transition(PhaseInput.Succeeded);

        var result = sm.Transition(PhaseInput.Failed);
        Assert.Equal(GoalPhase.Coding, result.NextPhase);
        Assert.Equal(TransitionEffect.NewIteration, result.Effect);
    }

    [Fact]
    public void Failed_AtImprove_IsNonBlocking()
    {
        var sm = CreateStarted(PlanWithImprove);
        while (sm.Phase != GoalPhase.Improve)
            sm.Transition(PhaseInput.Succeeded);

        var result = sm.Transition(PhaseInput.Failed);
        Assert.Equal(GoalPhase.Merging, result.NextPhase);
        Assert.Equal(TransitionEffect.Continue, result.Effect);
    }

    // ---- Succeeded at all active phases advances to next ----

    [Fact]
    public void Succeeded_AtEveryPhase_AdvancesToNext()
    {
        var sm = CreateStarted(PlanWithImprove);

        var expected = new[]
        {
            GoalPhase.Testing, GoalPhase.DocWriting, GoalPhase.Review,
            GoalPhase.Improve, GoalPhase.Merging, GoalPhase.Done
        };

        foreach (var expectedNext in expected)
        {
            var result = sm.Transition(PhaseInput.Succeeded);
            Assert.Equal(expectedNext, result.NextPhase);
        }
    }
}
