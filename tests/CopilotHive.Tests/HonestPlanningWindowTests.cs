using CopilotHive.Dashboard;
using CopilotHive.Git;
using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;
using CopilotHive.Workers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CopilotHive.Tests;

/// <summary>
/// Contract tests for the "honest Planning window": the minutes-long re-plan that follows a
/// review reject, testing failure, doc-writer RequestChanges or merge failure.
/// <para>
/// Before this contract existed the state machine claimed <see cref="GoalPhase.Coding"/> with an
/// EMPTY phase queue while re-planning ran. The dashboard showed the stale previous phase, and a
/// late/duplicate task completion arriving in that window flowed into
/// <see cref="PipelineStateMachine.Transition"/> and killed the goal via the empty-queue
/// invariant (a recorded incident that lost ~3h of work).
/// </para>
/// <para>Three cooperating pieces make the window safe, and each is pinned here:</para>
/// <list type="number">
///   <item>the machine lands in <see cref="GoalPhase.Planning"/> with an EMPTY queue, so
///     <c>Transition</c> cannot advance anywhere;</item>
///   <item>the driver opens the window as its FIRST operation so the pipeline phase is honest for
///     the whole re-plan (which is what makes the dashboard planning step render active);</item>
///   <item><see cref="TaskCompletionService"/> drops any completion arriving in the window before
///     it can reach the state machine.</item>
/// </list>
/// <para>
/// The plan is deliberately RETAINED through the window (it is the re-planning context the Brain
/// reads) and only replaced by <c>SetPlan</c> at the install — asserted below.
/// </para>
/// All mid-window observations use a <see cref="TaskCompletionSource"/> gate that parks the fake
/// brain inside planning; there is no polling or timing dependency anywhere in this class.
/// </summary>
public sealed class HonestPlanningWindowTests
{
    private static readonly List<GoalPhase> StandardPlanPhases =
        [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging];

    /// <summary>Deterministic upper bound for gate waits: a hang is a failure, not a slow test.</summary>
    private static readonly TimeSpan GateTimeout = TimeSpan.FromSeconds(30);

    // ══════════════════════════════════════════════════════════════════════
    //  THE MACHINE
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Every existing NewIteration input must land in Planning with the phase queue EMPTIED.
    /// The empty queue is what makes the window safe: <c>Transition</c> has nowhere to advance,
    /// so no completion can push the pipeline forward while re-planning is in flight.
    /// </summary>
    [Theory]
    [InlineData(GoalPhase.Coding, PhaseInput.Failed)]
    [InlineData(GoalPhase.Testing, PhaseInput.Failed)]
    [InlineData(GoalPhase.DocWriting, PhaseInput.Failed)]
    [InlineData(GoalPhase.DocWriting, PhaseInput.RequestChanges)]
    [InlineData(GoalPhase.Review, PhaseInput.Failed)]
    [InlineData(GoalPhase.Review, PhaseInput.RequestChanges)]
    [InlineData(GoalPhase.Merging, PhaseInput.Failed)]
    public void NewIterationInput_LandsInPlanning_WithEmptyQueue(GoalPhase from, PhaseInput input)
    {
        var sm = new PipelineStateMachine();
        sm.RestoreFromPlan(
            [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.DocWriting, GoalPhase.Review, GoalPhase.Merging],
            from);
        Assert.Equal(from, sm.Phase);

        var result = sm.Transition(input);

        // The transition REPORTS Planning and the machine IS in Planning — both halves matter:
        // the driver switches on the effect and mirrors the reported phase onto the pipeline.
        Assert.Equal(TransitionEffect.NewIteration, result.Effect);
        Assert.Equal(GoalPhase.Planning, result.NextPhase);
        Assert.Equal(GoalPhase.Planning, sm.Phase);
        Assert.NotEqual(GoalPhase.Coding, sm.Phase);

        // The queue is emptied — nothing to advance to while the window is open.
        Assert.Empty(sm.RemainingPhases);
        Assert.Empty(sm.CompletedPhases);
    }

    /// <summary>
    /// The install ends the window: <c>StartIteration</c> makes the FIRST PLANNED phase current
    /// (never an assumed Coding phase) and re-fills the queue with the rest of the plan.
    /// </summary>
    [Theory]
    [InlineData(GoalPhase.Coding)]
    [InlineData(GoalPhase.DocWriting)]
    public void StartIteration_AfterPlanningWindow_InstallsFirstPlannedPhase(GoalPhase firstPlanned)
    {
        var sm = new PipelineStateMachine();
        sm.RestoreFromPlan(StandardPlanPhases, GoalPhase.Review);
        sm.Transition(PhaseInput.RequestChanges);
        Assert.Equal(GoalPhase.Planning, sm.Phase);

        List<GoalPhase> newPlan = firstPlanned == GoalPhase.Coding
            ? [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging]
            : [GoalPhase.DocWriting, GoalPhase.Coding, GoalPhase.Merging];

        sm.StartIteration(newPlan);

        Assert.Equal(firstPlanned, sm.Phase);
        Assert.Equal(newPlan.Skip(1), sm.RemainingPhases);
        Assert.Empty(sm.CompletedPhases);
    }

    /// <summary>
    /// The backstop is intact: while the window is open EVERY input is rejected with the existing
    /// "Call StartIteration()" error. Nothing can slip past the empty queue into
    /// <c>AdvanceToNext</c>'s "No remaining phases" throw.
    /// </summary>
    [Theory]
    [InlineData(PhaseInput.Succeeded)]
    [InlineData(PhaseInput.Failed)]
    [InlineData(PhaseInput.RequestChanges)]
    public void Transition_DuringPlanningWindow_ThrowsStartIterationBackstop(PhaseInput input)
    {
        var sm = new PipelineStateMachine();
        sm.RestoreFromPlan(StandardPlanPhases, GoalPhase.Review);
        sm.Transition(PhaseInput.Failed);
        Assert.Equal(GoalPhase.Planning, sm.Phase);

        var ex = Assert.Throws<InvalidOperationException>(() => sm.Transition(input));
        Assert.Equal("Call StartIteration() before transitioning from Planning.", ex.Message);

        // The rejected input left the machine untouched — still the honest, empty window.
        Assert.Equal(GoalPhase.Planning, sm.Phase);
        Assert.Empty(sm.RemainingPhases);
    }

    /// <summary>
    /// The empty-queue throw is UNREACHABLE from the completion path: the
    /// <see cref="TaskCompletionService"/> Planning guard drops the completion before any
    /// <c>Transition</c> call. This is the incident invariant in miniature — if the guard is
    /// removed, <c>DriveNextPhaseAsync</c> reaches <c>Transition</c>, which throws (proven
    /// directly below), and <c>TaskCompletionService</c>'s catch-all marks the goal FAILED.
    /// </summary>
    [Fact]
    public async Task Completion_DuringPlanningWindow_NeverReachesTransition_SoGoalSurvives()
    {
        var h = CreateHarness();
        OpenPlanningWindow(h.Pipeline);
        h.Pipeline.SetActiveTask(h.TaskId);
        var phaseLogBefore = h.Pipeline.PhaseLog.Count;

        // Proof that reaching Transition in THIS state would be fatal: a separate machine put
        // into the identical window state throws, and TaskCompletionService's catch-all would
        // turn that throw into MarkGoalFailedAsync. The real pipeline below must never get there.
        var probe = new PipelineStateMachine();
        probe.RestoreFromPlan(StandardPlanPhases, GoalPhase.Review);
        probe.Transition(PhaseInput.Failed);
        Assert.Equal(GoalPhase.Planning, probe.Phase);
        Assert.Throws<InvalidOperationException>(() => probe.Transition(PhaseInput.Succeeded));

        await h.CompletionService.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = h.TaskId,
            Status = TaskOutcome.Completed,
            Output = "late worker result",
            Metrics = new TaskMetrics { Verdict = Verdict.Pass },
        }, TestContext.Current.CancellationToken);

        // The goal is ALIVE: no terminal transition, no failure persisted, no phase movement.
        Assert.Equal(GoalPhase.Planning, h.Pipeline.Phase);
        Assert.Equal(GoalPhase.Planning, h.Pipeline.StateMachine.Phase);
        Assert.DoesNotContain(h.Store.Updates, u => u.Status == GoalStatus.Failed);
        Assert.Equal(phaseLogBefore, h.Pipeline.PhaseLog.Count);
        Assert.Null(h.Goal.FailureReason);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  THE DRIVER — HandleNewIterationAsync
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The window is honest from the FIRST operation of <c>HandleNewIterationAsync</c>: while
    /// re-planning is parked on the gate this asserts the complete mid-window contract —
    /// pipeline phase, machine phase, plan retention, an untouched PhaseLog, no completed summary
    /// for the live iteration, and the dashboard planning step rendering ACTIVE with no component
    /// change (the timeline derives the live row from <c>pipeline.Phase == Planning</c>).
    /// </summary>
    [Fact]
    public async Task HandleNewIterationAsync_WhileReplanning_WindowIsHonest_PlanRetained_DashboardPlanningActive()
    {
        var gate = new PlanGate();
        var h = CreateHarness(resolvePlan: gate.ResolvePlanAsync);

        var previousPlan = new IterationPlan
        {
            Phases = StandardPlanPhases,
            Reason = "previous iteration plan",
        };
        h.Pipeline.SetPlan(previousPlan);
        h.Pipeline.StateMachine.RestoreFromPlan(StandardPlanPhases, GoalPhase.Review);
        h.Pipeline.AdvanceTo(GoalPhase.Review);

        var reviewEntry = PhaseResult.Create(GoalPhase.Review, h.Pipeline.Iteration, 1);
        reviewEntry.Result = PhaseOutcome.Fail;
        reviewEntry.CompletedAt = DateTime.UtcNow;
        h.Pipeline.PhaseLog.Add(reviewEntry);
        var phaseLogSnapshot = h.Pipeline.PhaseLog.ToList();
        var endingIteration = h.Pipeline.Iteration;

        // The machine produces the NewIteration effect exactly as production does.
        var transition = h.Pipeline.StateMachine.Transition(PhaseInput.Failed);
        Assert.Equal(TransitionEffect.NewIteration, transition.Effect);

        var driving = h.Driver.HandleNewIterationAsync(h.Pipeline, Verdict.Fail, TestContext.Current.CancellationToken);

        // ── Mid-window: re-planning is in flight, parked inside the brain ──
        await gate.Entered.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        // 1. Both the pipeline AND the machine report the honest Planning window.
        Assert.Equal(GoalPhase.Planning, h.Pipeline.Phase);
        Assert.Equal(GoalPhase.Planning, h.Pipeline.StateMachine.Phase);

        // 2. Planning observed the honest phase — never an assumed Coding phase.
        Assert.Equal([GoalPhase.Planning], gate.ObservedPhases);
        Assert.DoesNotContain(GoalPhase.Coding, gate.ObservedPhases);

        // 3. PLAN RETENTION: the PREVIOUS iteration's plan is still installed. This is the
        //    re-planning context the Brain reads; nothing calls ClearPlan.
        Assert.Same(previousPlan, h.Pipeline.Plan);

        // 4. The window transition added NO pseudo PhaseLog entry (AdvanceTo does not log).
        Assert.Equal(phaseLogSnapshot.Count, h.Pipeline.PhaseLog.Count);
        Assert.Equal(phaseLogSnapshot, h.Pipeline.PhaseLog);

        // 5. THE DASHBOARD BINDING PROOF — no UI component change is needed. There is no
        //    completed summary for the LIVE iteration, so the timeline derives a live row from
        //    pipeline state, and its Planning step renders "active" because Phase == Planning.
        //    (The summary reorder is what guarantees no live-iteration summary exists: the
        //    ending iteration's summary is filed under the PREVIOUS iteration number.)
        var liveIteration = h.Pipeline.Iteration;
        Assert.DoesNotContain(h.Pipeline.CompletedIterationSummaries, s => s.Iteration == liveIteration);

        var timeline = GoalDetailViewBuilder.BuildIterationTimeline(h.Goal, h.Pipeline.GoalId, h.Pipeline);
        var liveRow = Assert.Single(timeline, i => i.Number == liveIteration);
        Assert.True(liveRow.IsCurrent);
        var planningStep = Assert.Single(liveRow.Phases, p => p.Name == "Planning");
        Assert.Equal("active", planningStep.Status);

        // The ending iteration is shown as a completed (non-current) row, so the window does not
        // hide history while it is open.
        var endedRow = Assert.Single(timeline, i => i.Number == endingIteration);
        Assert.False(endedRow.IsCurrent);

        // 6. ENTRY-ORDER PROOF (pre-planning observable). The summary persistence runs BEFORE
        //    re-planning, so its sampled phase pins the transition to an operation EARLIER than
        //    resolvePlan. If AdvanceTo(Planning) were moved down to just before resolvePlan, this
        //    persist would still observe the STALE Review phase and this assertion would fail —
        //    the mid-window assertions above cannot catch that reorder on their own.
        var windowPersist = Assert.Single(
            h.Store.Updates, u => u.Status == GoalStatus.InProgress && u.Metadata?.IterationSummary is not null);
        Assert.Equal(GoalPhase.Planning, windowPersist.PipelinePhaseAtCall);
        Assert.NotEqual(GoalPhase.Review, windowPersist.PipelinePhaseAtCall);

        // ── Release planning and let the install run ──
        gate.Release();
        await driving.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        // THE INSTALL: SetPlan REPLACES the retained plan and the pipeline lands on the first
        // planned phase — the window is closed.
        Assert.NotSame(previousPlan, h.Pipeline.Plan);
        Assert.Same(gate.InstalledPlan, h.Pipeline.Plan);
        Assert.Equal(gate.InstalledPlan!.Phases[0], h.Pipeline.Phase);
        Assert.Equal(gate.InstalledPlan.Phases[0], h.Pipeline.StateMachine.Phase);
        Assert.Equal(gate.InstalledPlan.Phases.Skip(1), h.Pipeline.StateMachine.RemainingPhases);
    }

    /// <summary>
    /// The summary reorder, both halves at once. The summary is BUILT before
    /// <c>IterationBudget.TryConsume()</c> — so it snapshots the ENDING iteration — but ADDED and
    /// PERSISTED only after the consume succeeds, which the recording store proves by sampling
    /// <c>pipeline.Iteration</c> at call time: the summary's iteration is the OLD number while the
    /// pipeline has already moved to the NEW one.
    /// </summary>
    [Fact]
    public async Task HandleNewIterationAsync_BudgetConsumed_SummaryBuiltBeforeConsume_PersistedAfter()
    {
        var h = CreateHarness();
        SeedFailedReviewIteration(h.Pipeline);
        var endingIteration = h.Pipeline.Iteration;

        h.Pipeline.StateMachine.Transition(PhaseInput.Failed);
        await h.Driver.HandleNewIterationAsync(h.Pipeline, Verdict.Fail, TestContext.Current.CancellationToken);

        Assert.Equal(endingIteration + 1, h.Pipeline.Iteration);

        var update = Assert.Single(h.Store.Updates, u => u.Metadata?.IterationSummary is not null);
        Assert.Equal(GoalStatus.InProgress, update.Status);

        // BUILT BEFORE the consume: the snapshot carries the ENDING iteration number.
        Assert.Equal(endingIteration, update.Metadata!.IterationSummary!.Iteration);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(update.Metadata.IterationSummary.Phases, p => p.Name == GoalPhase.Review).Result);

        // PERSISTED AFTER the consume: at call time the pipeline had already advanced.
        Assert.Equal(endingIteration + 1, update.PipelineIterationAtCall);

        // …and the in-memory list agrees.
        var completed = Assert.Single(h.Pipeline.CompletedIterationSummaries);
        Assert.Equal(endingIteration, completed.Iteration);
    }

    /// <summary>
    /// Iteration-budget exhaustion from inside the window: the goal fails and the already-built
    /// summary is DROPPED — never added, never persisted. <c>FinalizeGoalAsync</c> owns the
    /// terminal summary, so adding one here would duplicate it in the dashboard tab bar.
    /// </summary>
    [Fact]
    public async Task HandleNewIterationAsync_IterationBudgetExhausted_FailsAndDropsBuiltSummary()
    {
        var h = CreateHarness();
        SeedFailedReviewIteration(h.Pipeline);
        ExhaustIterationBudget(h.Pipeline);
        var summariesBefore = h.Pipeline.CompletedIterationSummaries.Count;
        var iterationAtFailure = h.Pipeline.Iteration;

        h.Pipeline.StateMachine.Transition(PhaseInput.Failed);
        await h.Driver.HandleNewIterationAsync(h.Pipeline, Verdict.Fail, TestContext.Current.CancellationToken);

        // Terminal path, entered from the honest Planning window.
        Assert.Equal(GoalPhase.Failed, h.Pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, h.Pipeline.StateMachine.Phase);
        Assert.Equal("Exceeded max iterations", h.Goal.FailureReason);

        // Exactly ONE summary was added — FinalizeGoalAsync's terminal one, not a window snapshot.
        Assert.Equal(summariesBefore + 1, h.Pipeline.CompletedIterationSummaries.Count);
        var only = Assert.Single(h.Pipeline.CompletedIterationSummaries);
        Assert.Equal(iterationAtFailure, only.Iteration);

        // Exactly ONE persisted summary, and it is the Failed one — no InProgress window persist.
        var update = Assert.Single(h.Store.Updates, u => u.Metadata?.IterationSummary is not null);
        Assert.Equal(GoalStatus.Failed, update.Status);
        Assert.DoesNotContain(h.Store.Updates,
            u => u.Status == GoalStatus.InProgress && u.Metadata?.IterationSummary is not null);

        // TERMINAL-PATH PHASE CONTRACT: persisted from Failed, never from the window phase.
        Assert.Equal(GoalPhase.Failed, update.PipelinePhaseAtCall);
    }

    /// <summary>
    /// The retry-budget early-fail terminal path is unchanged by the window: it now simply runs
    /// from Planning, and still fails the goal without persisting a window summary.
    /// </summary>
    [Fact]
    public async Task HandleNewIterationAsync_RetryBudgetExhausted_FailsFromWindowWithoutSummary()
    {
        var h = CreateHarness(maxRetries: 0);
        SeedFailedReviewIteration(h.Pipeline);

        h.Pipeline.StateMachine.Transition(PhaseInput.Failed);
        await h.Driver.HandleNewIterationAsync(h.Pipeline, Verdict.Fail, TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Failed, h.Pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, h.Pipeline.StateMachine.Phase);
        Assert.Equal("Exceeded max test retries", h.Goal.FailureReason);

        // No InProgress window summary: only the terminal one from FinalizeGoalAsync.
        var update = Assert.Single(h.Store.Updates, u => u.Metadata?.IterationSummary is not null);
        Assert.Equal(GoalStatus.Failed, update.Status);

        // TERMINAL-PATH PHASE CONTRACT. The terminal block advances to Failed BEFORE invoking
        // the lifecycle service, so every persistence seam on this path must observe Failed —
        // the goal must never be persisted mid-terminal under the transient window phase.
        Assert.Equal(GoalPhase.Failed, update.PipelinePhaseAtCall);
        Assert.All(h.Store.Updates, u => Assert.Equal(GoalPhase.Failed, u.PipelinePhaseAtCall));
    }

    /// <summary>
    /// Caller cancellation (service shutdown) mid-window propagates instead of being converted
    /// into a spurious goal failure — the pipeline is simply left in the honest Planning window.
    /// </summary>
    [Fact]
    public async Task HandleNewIterationAsync_CallerCancelledMidWindow_PropagatesWithoutFailingGoal()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var h = CreateHarness(resolvePlan: (_, _, ct) => throw new OperationCanceledException(ct));
        SeedFailedReviewIteration(h.Pipeline);
        h.Pipeline.StateMachine.Transition(PhaseInput.Failed);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Driver.HandleNewIterationAsync(h.Pipeline, Verdict.Fail, cts.Token));

        // No spurious Failed, and the window phase is the honest Planning — never Coding.
        Assert.Equal(GoalPhase.Planning, h.Pipeline.Phase);
        Assert.NotEqual(GoalPhase.Coding, h.Pipeline.Phase);
        Assert.DoesNotContain(h.Store.Updates, u => u.Status == GoalStatus.Failed);
        Assert.Null(h.Goal.FailureReason);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  THE DRIVER — HandleMergeFailureAsync (same proofs, same placement)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The merge-failure window gets the identical treatment: <c>AdvanceTo(Planning)</c> is the
    /// FIRST operation (it runs after the machine already produced the NewIteration effect), the
    /// merging-entry fail-marking is preserved, the plan is retained, no pseudo PhaseLog entry is
    /// added, and the dashboard planning step renders active.
    /// </summary>
    [Fact]
    public async Task HandleMergeFailureAsync_WhileReplanning_WindowIsHonest_MergingMarkedFailed_PlanRetained()
    {
        const string mergeError = "conflict in Program.cs";
        var gate = new PlanGate();
        var h = CreateHarness(resolvePlan: gate.ResolvePlanAsync);

        var previousPlan = new IterationPlan { Phases = StandardPlanPhases, Reason = "previous iteration plan" };
        h.Pipeline.SetPlan(previousPlan);
        h.Pipeline.StateMachine.RestoreFromPlan(StandardPlanPhases, GoalPhase.Merging);
        h.Pipeline.AdvanceTo(GoalPhase.Merging);

        var mergingEntry = PhaseResult.Create(GoalPhase.Merging, h.Pipeline.Iteration, 1);
        h.Pipeline.PhaseLog.Add(mergingEntry);
        var phaseLogSnapshot = h.Pipeline.PhaseLog.ToList();

        // Production reaches HandleMergeFailureAsync only after this transition.
        var transition = h.Pipeline.StateMachine.Transition(PhaseInput.Failed);
        Assert.Equal(TransitionEffect.NewIteration, transition.Effect);
        Assert.Equal(GoalPhase.Planning, h.Pipeline.StateMachine.Phase);

        var driving = h.Driver.HandleMergeFailureAsync(h.Pipeline, mergeError, TestContext.Current.CancellationToken);

        await gate.Entered.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Planning, h.Pipeline.Phase);
        Assert.Equal(GoalPhase.Planning, h.Pipeline.StateMachine.Phase);
        Assert.Equal([GoalPhase.Planning], gate.ObservedPhases);

        // Plan retention through the merge-failure window too.
        Assert.Same(previousPlan, h.Pipeline.Plan);

        // The merging-entry fail-marking survives the reorder (dashboard shows WHY it failed)…
        Assert.Equal(PhaseOutcome.Fail, mergingEntry.Result);
        Assert.Equal(mergeError, mergingEntry.WorkerOutput);
        Assert.NotNull(mergingEntry.CompletedAt);

        // …while no pseudo entry was appended by the window transition.
        Assert.Equal(phaseLogSnapshot.Count, h.Pipeline.PhaseLog.Count);
        Assert.Equal(phaseLogSnapshot, h.Pipeline.PhaseLog);

        // Dashboard binding proof, same mechanism as the new-iteration window.
        var liveIteration = h.Pipeline.Iteration;
        Assert.DoesNotContain(h.Pipeline.CompletedIterationSummaries, s => s.Iteration == liveIteration);
        var timeline = GoalDetailViewBuilder.BuildIterationTimeline(h.Goal, h.Pipeline.GoalId, h.Pipeline);
        var liveRow = Assert.Single(timeline, i => i.Number == liveIteration);
        Assert.True(liveRow.IsCurrent);
        Assert.Equal("active", Assert.Single(liveRow.Phases, p => p.Name == "Planning").Status);

        // ENTRY-ORDER PROOF (pre-planning observable), same mechanism as the new-iteration side:
        // the summary persistence runs BEFORE resolvePlan, so its sampled phase pins the
        // transition ahead of the merging-entry handling and the budget checks. Moving
        // AdvanceTo(Planning) down to just before resolvePlan leaves this persist observing the
        // STALE Merging phase and fails the assertion.
        var windowPersist = Assert.Single(
            h.Store.Updates, u => u.Status == GoalStatus.InProgress && u.Metadata?.IterationSummary is not null);
        Assert.Equal(GoalPhase.Planning, windowPersist.PipelinePhaseAtCall);
        Assert.NotEqual(GoalPhase.Merging, windowPersist.PipelinePhaseAtCall);

        gate.Release();
        await driving.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        // The install replaces the retained plan and lands on the first planned phase.
        Assert.NotSame(previousPlan, h.Pipeline.Plan);
        Assert.Same(gate.InstalledPlan, h.Pipeline.Plan);
        Assert.Equal(gate.InstalledPlan!.Phases[0], h.Pipeline.Phase);
        Assert.Equal(gate.InstalledPlan.Phases[0], h.Pipeline.StateMachine.Phase);
    }

    /// <summary>
    /// The merge-failure summary reorder: built before the <c>TryConsume</c> (so it snapshots the
    /// ending iteration) and added/persisted after it.
    /// </summary>
    [Fact]
    public async Task HandleMergeFailureAsync_SummaryBuiltBeforeConsume_PersistedAfter()
    {
        var h = CreateHarness();
        h.Pipeline.StateMachine.RestoreFromPlan(StandardPlanPhases, GoalPhase.Merging);
        h.Pipeline.AdvanceTo(GoalPhase.Merging);
        h.Pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Merging, h.Pipeline.Iteration, 1));
        var endingIteration = h.Pipeline.Iteration;

        h.Pipeline.StateMachine.Transition(PhaseInput.Failed);
        await h.Driver.HandleMergeFailureAsync(h.Pipeline, "conflict", TestContext.Current.CancellationToken);

        Assert.Equal(endingIteration + 1, h.Pipeline.Iteration);

        var update = Assert.Single(h.Store.Updates, u => u.Metadata?.IterationSummary is not null);
        Assert.Equal(GoalStatus.InProgress, update.Status);
        Assert.Equal(endingIteration, update.Metadata!.IterationSummary!.Iteration);
        Assert.Equal(PhaseOutcome.Fail,
            Assert.Single(update.Metadata.IterationSummary.Phases, p => p.Name == GoalPhase.Merging).Result);
        Assert.Equal(endingIteration + 1, update.PipelineIterationAtCall);
    }

    /// <summary>
    /// Both merge-failure budget early-fail terminal paths still fail the goal from the window
    /// without persisting a window summary — FinalizeGoalAsync's terminal summary owns the record.
    /// </summary>
    [Theory]
    [InlineData(true)]  // review retry budget exhausted
    [InlineData(false)] // iteration budget exhausted
    public async Task HandleMergeFailureAsync_BudgetExhausted_FailsFromWindowWithoutWindowSummary(bool retryBudget)
    {
        var h = CreateHarness(maxRetries: retryBudget ? 0 : 3);
        h.Pipeline.StateMachine.RestoreFromPlan(StandardPlanPhases, GoalPhase.Merging);
        h.Pipeline.AdvanceTo(GoalPhase.Merging);

        // Exhaust BEFORE seeding the entry: consuming the budget advances pipeline.Iteration, and
        // the merging entry must belong to the iteration that is actually failing.
        if (!retryBudget)
            ExhaustIterationBudget(h.Pipeline);

        var mergingEntry = PhaseResult.Create(GoalPhase.Merging, h.Pipeline.Iteration, 1);
        h.Pipeline.PhaseLog.Add(mergingEntry);

        h.Pipeline.StateMachine.Transition(PhaseInput.Failed);
        await h.Driver.HandleMergeFailureAsync(h.Pipeline, "conflict", TestContext.Current.CancellationToken);

        Assert.Equal(GoalPhase.Failed, h.Pipeline.Phase);
        Assert.Equal(GoalPhase.Failed, h.Pipeline.StateMachine.Phase);

        // The merging entry was marked failed BEFORE the terminal exit either way.
        Assert.Equal(PhaseOutcome.Fail, mergingEntry.Result);

        var update = Assert.Single(h.Store.Updates, u => u.Metadata?.IterationSummary is not null);
        Assert.Equal(GoalStatus.Failed, update.Status);
        Assert.DoesNotContain(h.Store.Updates,
            u => u.Status == GoalStatus.InProgress && u.Metadata?.IterationSummary is not null);

        // TERMINAL-PATH PHASE CONTRACT for BOTH merge budget early-fail paths: the terminal
        // block advances to Failed before the lifecycle call, so every persistence seam here
        // observes Failed — never the transient window phase.
        Assert.Equal(GoalPhase.Failed, update.PipelinePhaseAtCall);
        Assert.All(h.Store.Updates, u => Assert.Equal(GoalPhase.Failed, u.PipelinePhaseAtCall));
    }

    /// <summary>Caller cancellation mid merge-failure window propagates; no spurious failure.</summary>
    [Fact]
    public async Task HandleMergeFailureAsync_CallerCancelledMidWindow_PropagatesWithoutFailingGoal()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var h = CreateHarness(resolvePlan: (_, _, ct) => throw new OperationCanceledException(ct));
        h.Pipeline.StateMachine.RestoreFromPlan(StandardPlanPhases, GoalPhase.Merging);
        h.Pipeline.AdvanceTo(GoalPhase.Merging);
        h.Pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Merging, h.Pipeline.Iteration, 1));

        h.Pipeline.StateMachine.Transition(PhaseInput.Failed);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Driver.HandleMergeFailureAsync(h.Pipeline, "conflict", cts.Token));

        Assert.Equal(GoalPhase.Planning, h.Pipeline.Phase);
        Assert.NotEqual(GoalPhase.Coding, h.Pipeline.Phase);
        Assert.DoesNotContain(h.Store.Updates, u => u.Status == GoalStatus.Failed);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  ENTRY ORDER — the window opens before the EARLY operations, not just before planning
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Pins <c>AdvanceTo(Planning)</c> ahead of the EARLY operations by observing the summary
    /// persistence — an operation that runs strictly BEFORE re-planning starts. The mid-window
    /// TCS-gate tests observe state only once <c>resolvePlan</c> is entered, so on their own they
    /// cannot distinguish "window opened first" from "window opened just before planning": the
    /// stale interval covering the budget checks and the persistence would go unnoticed.
    /// Observing HERE closes that hole — the phase is sampled synchronously inside the
    /// persistence call, so a transition moved past it observes the stale pre-planning phase.
    /// </summary>
    [Theory]
    [InlineData(false)] // HandleNewIterationAsync, entered from Review
    [InlineData(true)]  // HandleMergeFailureAsync, entered from Merging
    public async Task Window_OpensBeforeSummaryPersistence_NotMerelyBeforeReplanning(bool mergeHandler)
    {
        var entryPhase = mergeHandler ? GoalPhase.Merging : GoalPhase.Review;
        GoalPhase? phaseDuringPersist = null;
        bool? planningEnteredAtPersist = null;
        var planningEntered = false;

        var h = CreateHarness(resolvePlan: (_, _, _) =>
        {
            planningEntered = true;
            return Task.FromResult(PlanResult.Success(new IterationPlan
            {
                Phases = [GoalPhase.Coding, GoalPhase.Merging],
                Reason = "re-planned",
            }));
        });

        // Sampled SYNCHRONOUSLY inside the pre-planning persistence call, capturing both the phase
        // and whether re-planning had run yet. No blocking wait here: the driver reaches this
        // point synchronously, so parking the thread would deadlock the handler under test.
        h.Store.OnUpdate = (status, phaseAtCall) =>
        {
            if (status != GoalStatus.InProgress || phaseDuringPersist is not null)
                return;

            phaseDuringPersist = phaseAtCall;
            planningEnteredAtPersist = planningEntered;
        };

        h.Pipeline.SetPlan(new IterationPlan { Phases = StandardPlanPhases, Reason = "previous plan" });
        h.Pipeline.StateMachine.RestoreFromPlan(StandardPlanPhases, entryPhase);
        h.Pipeline.AdvanceTo(entryPhase);

        var entry = PhaseResult.Create(entryPhase, h.Pipeline.Iteration, 1);
        entry.Result = PhaseOutcome.Fail;
        entry.CompletedAt = DateTime.UtcNow;
        h.Pipeline.PhaseLog.Add(entry);

        h.Pipeline.StateMachine.Transition(PhaseInput.Failed);

        if (mergeHandler)
            await h.Driver.HandleMergeFailureAsync(h.Pipeline, "conflict", TestContext.Current.CancellationToken);
        else
            await h.Driver.HandleNewIterationAsync(h.Pipeline, Verdict.Fail, TestContext.Current.CancellationToken);

        // The persistence really did happen, and it happened BEFORE re-planning — that ordering is
        // what makes this observation stronger than the mid-window gate.
        Assert.NotNull(phaseDuringPersist);
        Assert.False(planningEnteredAtPersist);

        // THE DISCRIMINATOR: the window was already open when this EARLY operation ran. With
        // AdvanceTo(Planning) moved after the budget checks / merge-entry handling, this sample
        // would be the stale entry phase instead.
        Assert.Equal(GoalPhase.Planning, phaseDuringPersist);
        Assert.NotEqual(entryPhase, phaseDuringPersist);

        // EARLIER STILL, for the merge handler: the driver logs the rebase-retry line between the
        // merging-entry fail-marking / budget checks and the summary build. Sampling the phase at
        // THAT log pins the transition ahead of those operations too — a transition moved to any
        // point after them (even one still before the persistence) observes the stale phase here.
        if (mergeHandler)
        {
            var rebaseLog = Assert.Single(
                h.DriverLogger.Entries, e => e.Message.Contains("sending back to Coder for rebase"));
            Assert.Equal(GoalPhase.Planning, rebaseLog.PhaseAtLog);
            Assert.NotEqual(entryPhase, rebaseLog.PhaseAtLog);
        }

        // Sanity: the run completed normally through the install.
        Assert.Equal(GoalPhase.Coding, h.Pipeline.Phase);
    }

    /// <summary>
    /// Structural backstop for the otherwise unobservable synchronous interval at handler entry,
    /// applied to BOTH re-plan handlers.
    /// <para>
    /// RetryBudget.TryConsume has no injectable seam and returns synchronously, so an
    /// AdvanceTo(Planning) moved immediately after that call cannot be distinguished by a
    /// post-call state assertion: every runtime observation point available to a test (the
    /// driver logger, the persistence hook, resolvePlan, the terminal-phase assertions) sits
    /// AFTER the budget consume, and would still see Planning. Pin the emitted call order
    /// directly instead: the first AdvanceTo call in the async state machine must precede the
    /// first retry-budget consume call.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(nameof(PipelineDriver.HandleNewIterationAsync))]
    [InlineData(nameof(PipelineDriver.HandleMergeFailureAsync))]
    public void ReplanHandler_AdvanceToPlanning_PrecedesRetryBudgetConsume(string handlerName)
    {
        var moveNext = GetAsyncMoveNext(handlerName);
        var advanceTo = typeof(GoalPipeline).GetMethod(nameof(GoalPipeline.AdvanceTo))!;
        var tryConsume = typeof(RetryBudget).GetMethod(nameof(RetryBudget.TryConsume))!;

        var advanceOffset = FindCallOffset(moveNext, advanceTo);
        var consumeOffset = FindCallOffset(moveNext, tryConsume);

        Assert.True(advanceOffset < consumeOffset,
            $"{handlerName}: AdvanceTo IL offset {advanceOffset} must precede " +
            $"TryConsume offset {consumeOffset}.");
    }

    /// <summary>
    /// The merge handler opens the window before its FIRST early operation — the PhaseLog
    /// merging-entry lookup and fail-marking — not merely before the budget consume.
    /// <para>
    /// This closes the interval the budget-consume backstop alone leaves open: moving
    /// AdvanceTo(Planning) to just before the rebase LogInformation would mutate the merging
    /// entry and consume the review-retry budget while the pipeline still reported Merging, yet
    /// every runtime observation point (driver logger, persistence hook, resolvePlan gate,
    /// terminal-phase assertions) occurs after that point and would still observe Planning.
    /// The merging-entry access is the earliest operation in the handler, so pinning
    /// AdvanceTo ahead of it pins it to the first statement.
    /// </para>
    /// </summary>
    [Fact]
    public void HandleMergeFailureAsync_AdvanceToPlanning_PrecedesMergeEntryAccessAndRetryBudgetConsume()
    {
        var moveNext = GetAsyncMoveNext(nameof(PipelineDriver.HandleMergeFailureAsync));
        var advanceTo = typeof(GoalPipeline).GetMethod(nameof(GoalPipeline.AdvanceTo))!;
        var phaseLogGetter = typeof(GoalPipeline).GetProperty(nameof(GoalPipeline.PhaseLog))!.GetMethod!;
        var tryConsume = typeof(RetryBudget).GetMethod(nameof(RetryBudget.TryConsume))!;

        var advanceOffset = FindCallOffset(moveNext, advanceTo);
        var mergeEntryOffset = FindCallOffset(moveNext, phaseLogGetter);
        var consumeOffset = FindCallOffset(moveNext, tryConsume);

        // The merging-entry access precedes the budget consume in the handler, so this is the
        // strictly stronger bound — it fails for ANY relocation past the first statement.
        Assert.True(advanceOffset < mergeEntryOffset,
            $"AdvanceTo IL offset {advanceOffset} must precede the merging-entry PhaseLog access " +
            $"at offset {mergeEntryOffset} — the window must open before the entry is mutated.");

        Assert.True(mergeEntryOffset < consumeOffset,
            $"Sanity: the merging-entry access at {mergeEntryOffset} is expected to precede the " +
            $"retry-budget consume at {consumeOffset}; if production reorders these, this test's " +
            "bound must be re-derived rather than silently weakened.");

        Assert.True(advanceOffset < consumeOffset,
            $"AdvanceTo IL offset {advanceOffset} must precede TryConsume offset {consumeOffset}.");
    }

    // ══════════════════════════════════════════════════════════════════════
    //  THE COMPLETION PATH — guard order is the classification
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Guard order, step 1: the terminal-state guard runs BEFORE the Planning guard. A completion
    /// arriving for a Done/Failed goal gets the EXISTING terminal classification verbatim even
    /// though the state machine sits in Planning (which it does after <c>Fail()</c>/a fresh
    /// pipeline) — it must never be re-classified as a planning-window drop.
    /// </summary>
    [Theory]
    [InlineData(GoalPhase.Done)]
    [InlineData(GoalPhase.Failed)]
    public async Task Completion_WhenGoalTerminal_UsesTerminalClassification_NotPlanningWindow(GoalPhase terminal)
    {
        var h = CreateHarness();
        OpenPlanningWindow(h.Pipeline);           // machine is in Planning…
        h.Pipeline.AdvanceTo(terminal);           // …but the goal is already terminal.
        h.Pipeline.SetActiveTask(h.TaskId);
        Assert.Equal(GoalPhase.Planning, h.Pipeline.StateMachine.Phase);

        await h.CompletionService.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = h.TaskId,
            Status = TaskOutcome.Completed,
            Output = "late result",
        }, TestContext.Current.CancellationToken);

        var terminalLog = Assert.Single(h.Logger.Logs, l =>
            l.Level == LogLevel.Information && l.Message.Contains("ignoring duplicate"));
        Assert.Equal(
            $"Task {h.TaskId} completed but goal {h.Goal.Id} already {terminal} — ignoring duplicate",
            terminalLog.Message);

        Assert.DoesNotContain(h.Logger.Logs, l => l.Message.Contains("reason=planning-window"));
        Assert.Equal(terminal, h.Pipeline.Phase);
    }

    /// <summary>
    /// Guard order, step 2: a completion arriving during the window is dropped with the
    /// StaleCompletion planning-window classification, at WARNING level, in the EXACT log format.
    /// No transition runs and the goal is not failed.
    /// </summary>
    [Fact]
    public async Task Completion_DuringPlanningWindow_LogsExactStaleCompletionFormat_AndDropsCleanly()
    {
        var h = CreateHarness();
        OpenPlanningWindow(h.Pipeline);
        h.Pipeline.SetActiveTask(h.TaskId);

        await h.CompletionService.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = h.TaskId,
            Status = TaskOutcome.Completed,
            Output = "worker finished during the re-plan",
            Metrics = new TaskMetrics { Verdict = Verdict.Pass },
        }, TestContext.Current.CancellationToken);

        var windowLog = Assert.Single(h.Logger.Logs, l => l.Message.Contains("reason=planning-window"));
        Assert.Equal(LogLevel.Warning, windowLog.Level);
        Assert.Equal(
            $"StaleCompletion goal={h.Goal.Id} task={h.TaskId} " +
            "pipeline-phase=Planning machine-phase=Planning reason=planning-window",
            windowLog.Message);

        // Dropped cleanly: no transition, no failure, no phase movement.
        Assert.Equal(GoalPhase.Planning, h.Pipeline.Phase);
        Assert.Equal(GoalPhase.Planning, h.Pipeline.StateMachine.Phase);
        Assert.DoesNotContain(h.Store.Updates, u => u.Status is GoalStatus.Failed or GoalStatus.Completed);
    }

    /// <summary>
    /// Guard order, step 3 — the classification-critical case: a completion whose task ID is NOT
    /// the active one, arriving DURING the window, must get the planning-window classification.
    /// If the Planning guard were moved after the stale-task guard this completion would be
    /// mis-classified as a plain stale task, hiding the window from the incident log.
    /// </summary>
    [Fact]
    public async Task Completion_DuringWindowWithNonActiveTaskId_UsesPlanningWindowClassification()
    {
        var h = CreateHarness();
        OpenPlanningWindow(h.Pipeline);

        var arrivingTaskId = h.TaskId;
        h.PipelineManager.RegisterTask("task-other-active", h.Goal.Id);
        h.Pipeline.SetActiveTask("task-other-active");   // arriving task is NOT active

        await h.CompletionService.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = arrivingTaskId,
            Status = TaskOutcome.Completed,
            Output = "stale-looking result during the re-plan",
        }, TestContext.Current.CancellationToken);

        var windowLog = Assert.Single(h.Logger.Logs, l => l.Message.Contains("reason=planning-window"));
        Assert.Equal(LogLevel.Warning, windowLog.Level);
        Assert.Contains($"task={arrivingTaskId}", windowLog.Message);

        // The OLD classification must be absent — this is the guard-order proof.
        Assert.DoesNotContain(h.Logger.Logs, l => l.Message.Contains("ignoring stale completion"));
        Assert.Equal(GoalPhase.Planning, h.Pipeline.StateMachine.Phase);
    }

    /// <summary>
    /// Guard order, step 4: OUTSIDE the window a non-matching task ID still gets the EXISTING
    /// stale-task classification, verbatim. The new guard did not swallow the old one.
    /// </summary>
    [Fact]
    public async Task Completion_WithNonActiveTaskIdOutsideWindow_UsesStaleTaskClassification()
    {
        var h = CreateHarness();
        h.Pipeline.StateMachine.RestoreFromPlan(StandardPlanPhases, GoalPhase.Testing);
        h.Pipeline.AdvanceTo(GoalPhase.Testing);
        h.PipelineManager.RegisterTask("task-current-phase", h.Goal.Id);
        h.Pipeline.SetActiveTask("task-current-phase");

        await h.CompletionService.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = h.TaskId,
            Status = TaskOutcome.Completed,
            Output = "stale completion",
        }, TestContext.Current.CancellationToken);

        var staleLog = Assert.Single(h.Logger.Logs, l => l.Message.Contains("ignoring stale completion"));
        Assert.Equal(LogLevel.Warning, staleLog.Level);
        Assert.Equal(
            $"Task {h.TaskId} completed but pipeline {h.Goal.Id} active task is task-current-phase " +
            "— ignoring stale completion",
            staleLog.Message);

        Assert.DoesNotContain(h.Logger.Logs, l => l.Message.Contains("reason=planning-window"));
        Assert.Equal(GoalPhase.Testing, h.Pipeline.Phase);
    }

    /// <summary>
    /// THE INCIDENT REPRODUCTION. Re-planning is parked mid-window when the previous phase's
    /// worker reports a late/duplicate result for the STILL-ACTIVE task id — the exact shape of
    /// the recorded incident. Before the guard this flowed into <c>Transition</c> and killed the
    /// goal via the empty-queue invariant. Now it drops with the planning-window classification,
    /// the goal stays alive, and the parked re-plan completes and installs the next iteration.
    /// </summary>
    [Fact]
    public async Task LateDuplicateCompletionDuringReplan_DropsCleanly_AndGoalSurvivesToNextIteration()
    {
        var gate = new PlanGate();
        var h = CreateHarness(resolvePlan: gate.ResolvePlanAsync);

        var previousPlan = new IterationPlan { Phases = StandardPlanPhases, Reason = "previous plan" };
        h.Pipeline.SetPlan(previousPlan);
        h.Pipeline.StateMachine.RestoreFromPlan(StandardPlanPhases, GoalPhase.Review);
        h.Pipeline.AdvanceTo(GoalPhase.Review);
        h.Pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Review, h.Pipeline.Iteration, 1));
        h.Pipeline.SetActiveTask(h.TaskId);

        // The reviewer rejects → the pipeline enters the re-plan window and parks on the gate.
        var reviewerResult = new TaskResult
        {
            TaskId = h.TaskId,
            Status = TaskOutcome.Completed,
            Output = "Rejected: several issues.",
            Metrics = new TaskMetrics { Verdict = Verdict.Fail },
        };
        var driving = h.CompletionService.HandleTaskCompletionAsync(
            reviewerResult, TestContext.Current.CancellationToken);

        await gate.Entered.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);
        Assert.Equal(GoalPhase.Planning, h.Pipeline.Phase);
        Assert.Equal(GoalPhase.Planning, h.Pipeline.StateMachine.Phase);

        // ── The incident: the SAME task reports again while re-planning is in flight ──
        await h.CompletionService.HandleTaskCompletionAsync(new TaskResult
        {
            TaskId = h.TaskId,
            Status = TaskOutcome.Completed,
            Output = "duplicate delivery of the reviewer result",
            Metrics = new TaskMetrics { Verdict = Verdict.Fail },
        }, TestContext.Current.CancellationToken);

        // Dropped with the planning-window classification — NOT the stale-task one (the task IS
        // still the active task, so only the Planning guard can catch it).
        var windowLog = Assert.Single(h.Logger.Logs, l => l.Message.Contains("reason=planning-window"));
        Assert.Equal(LogLevel.Warning, windowLog.Level);
        Assert.DoesNotContain(h.Logger.Logs, l => l.Message.Contains("ignoring stale completion"));

        // THE GOAL IS STILL ALIVE mid-window: not failed, not terminal, still in the window.
        Assert.Equal(GoalPhase.Planning, h.Pipeline.Phase);
        Assert.Equal(GoalPhase.Planning, h.Pipeline.StateMachine.Phase);
        Assert.DoesNotContain(h.Store.Updates, u => u.Status == GoalStatus.Failed);
        Assert.Null(h.Goal.FailureReason);

        // ── Re-planning completes and the goal continues into the next iteration ──
        gate.Release();
        await driving.WaitAsync(GateTimeout, TestContext.Current.CancellationToken);

        Assert.Equal(gate.InstalledPlan!.Phases[0], h.Pipeline.Phase);
        Assert.Equal(gate.InstalledPlan.Phases[0], h.Pipeline.StateMachine.Phase);
        Assert.NotSame(previousPlan, h.Pipeline.Plan);
        Assert.DoesNotContain(h.Store.Updates, u => u.Status == GoalStatus.Failed);
        Assert.Equal(2, h.Pipeline.Iteration);
    }

    /// <summary>
    /// The caller-cancellation contract survives the new guard: an <see cref="OperationCanceledException"/>
    /// from the drive path still propagates out of <c>HandleTaskCompletionAsync</c> rather than
    /// being swallowed or converted into a goal failure.
    /// </summary>
    [Fact]
    public async Task Completion_CallerCancelledDuringDrive_PropagatesAndDoesNotFailGoal()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var h = CreateHarness(resolvePlan: (_, _, ct) => throw new OperationCanceledException(ct));
        h.Pipeline.SetPlan(new IterationPlan { Phases = StandardPlanPhases });
        h.Pipeline.StateMachine.RestoreFromPlan(StandardPlanPhases, GoalPhase.Review);
        h.Pipeline.AdvanceTo(GoalPhase.Review);
        h.Pipeline.PhaseLog.Add(PhaseResult.Create(GoalPhase.Review, h.Pipeline.Iteration, 1));
        h.Pipeline.SetActiveTask(h.TaskId);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            h.CompletionService.HandleTaskCompletionAsync(new TaskResult
            {
                TaskId = h.TaskId,
                Status = TaskOutcome.Completed,
                Output = "Rejected.",
                Metrics = new TaskMetrics { Verdict = Verdict.Fail },
            }, cts.Token));

        Assert.DoesNotContain(h.Store.Updates, u => u.Status == GoalStatus.Failed);
        Assert.Null(h.Goal.FailureReason);
        Assert.Equal(GoalPhase.Planning, h.Pipeline.Phase);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves the compiler-generated <c>MoveNext</c> body for a public async handler on
    /// <see cref="PipelineDriver"/>, which is where the handler's emitted call sequence lives.
    /// </summary>
    private static MethodInfo GetAsyncMoveNext(string handlerName)
    {
        var handler = typeof(PipelineDriver).GetMethod(handlerName)
            ?? throw new Xunit.Sdk.XunitException($"No public method '{handlerName}' on PipelineDriver.");
        var stateMachine = handler.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType
            ?? throw new Xunit.Sdk.XunitException($"'{handlerName}' is not an async state machine.");
        return stateMachine.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

    /// <summary>Finds a direct call/callvirt to <paramref name="target"/> in emitted method IL.</summary>
    private static int FindCallOffset(MethodInfo caller, MethodInfo target)
    {
        var il = caller.GetMethodBody()!.GetILAsByteArray()!;
        var token = target.MetadataToken;

        for (var i = 0; i <= il.Length - 5; i++)
        {
            // call (0x28) and callvirt (0x6f) both carry a four-byte metadata token.
            if (il[i] is not (0x28 or 0x6f))
                continue;

            if (BitConverter.ToInt32(il, i + 1) == token)
                return i;
        }

        throw new Xunit.Sdk.XunitException(
            $"No emitted call from {caller.DeclaringType?.FullName}.{caller.Name} to " +
            $"{target.DeclaringType?.FullName}.{target.Name} was found.");
    }

    /// <summary>
    /// Drives the state machine from Review into the honest Planning window exactly as production
    /// does (a real <c>Transition</c> producing the NewIteration effect) and mirrors the window
    /// phase onto the pipeline, leaving the previous iteration's plan installed.
    /// </summary>
    private static void OpenPlanningWindow(GoalPipeline pipeline)
    {
        pipeline.SetPlan(new IterationPlan { Phases = StandardPlanPhases, Reason = "previous plan" });
        pipeline.StateMachine.RestoreFromPlan(StandardPlanPhases, GoalPhase.Review);
        pipeline.AdvanceTo(GoalPhase.Review);

        var transition = pipeline.StateMachine.Transition(PhaseInput.Failed);
        Assert.Equal(TransitionEffect.NewIteration, transition.Effect);
        Assert.Equal(GoalPhase.Planning, pipeline.StateMachine.Phase);

        pipeline.AdvanceTo(GoalPhase.Planning);
    }

    /// <summary>Seeds a pipeline sitting on a failed Review phase, ready to enter the window.</summary>
    private static void SeedFailedReviewIteration(GoalPipeline pipeline)
    {
        pipeline.SetPlan(new IterationPlan { Phases = StandardPlanPhases, Reason = "previous plan" });
        pipeline.StateMachine.RestoreFromPlan(StandardPlanPhases, GoalPhase.Review);
        pipeline.AdvanceTo(GoalPhase.Review);

        var entry = PhaseResult.Create(GoalPhase.Review, pipeline.Iteration, 1);
        entry.Result = PhaseOutcome.Fail;
        entry.CompletedAt = DateTime.UtcNow;
        pipeline.PhaseLog.Add(entry);
    }

    /// <summary>Consumes the whole iteration budget so the next TryConsume must fail.</summary>
    private static void ExhaustIterationBudget(GoalPipeline pipeline)
    {
        while (!pipeline.IterationBudget.IsExhausted)
            pipeline.IterationBudget.TryConsume();
        Assert.True(pipeline.IterationBudget.IsExhausted);
    }

    private sealed record Harness(
        PipelineDriver Driver,
        TaskCompletionService CompletionService,
        GoalPipeline Pipeline,
        GoalPipelineManager PipelineManager,
        Goal Goal,
        HpwRecordingGoalStore Store,
        HpwCapturingLogger<TaskCompletionService> Logger,
        string TaskId,
        HpwPhaseSamplingLogger<PipelineDriver> DriverLogger);

    /// <summary>
    /// Builds a self-contained driver + completion service over a recording goal store and a
    /// capturing logger. <paramref name="resolvePlan"/> defaults to an immediately successful
    /// plan; tests that need to observe the window supply a gated one.
    /// </summary>
    private static Harness CreateHarness(
        Func<GoalPipeline, string?, CancellationToken, Task<PlanResult>>? resolvePlan = null,
        int maxRetries = 3,
        int maxIterations = 5)
    {
        var goal = new Goal
        {
            Id = $"goal-{Guid.NewGuid():N}",
            Description = "Honest planning window test goal",
            RepositoryNames = ["test-repo"],
        };
        var store = new HpwRecordingGoalStore(goal);
        var goalManager = new GoalManager();
        goalManager.AddSource(store);

        var pipelineManager = new GoalPipelineManager();
        var pipeline = pipelineManager.CreatePipeline(goal, maxRetries, maxIterations);
        store.Pipeline = pipeline;

        var taskId = $"task-{Guid.NewGuid():N}";
        pipelineManager.RegisterTask(taskId, goal.Id);

        var lifecycleService = new GoalLifecycleService(goalManager, NullLogger<GoalLifecycleService>.Instance);

        var driverLogger = new HpwPhaseSamplingLogger<PipelineDriver>(() => pipeline.Phase);

        var driver = new PipelineDriver(
            brain: new HpwFakeBrain(),
            lifecycleService: lifecycleService,
            goalManager: goalManager,
            repoManager: new BrainRepoManager(Path.GetTempPath(), NullLogger<BrainRepoManager>.Instance),
            improvementAnalyzer: null,
            agentsManager: null,
            metricsTracker: null,
            dispatchToRole: (_, _, _, _) => Task.CompletedTask,
            resolvePrompt: (_, _, _, _) => Task.FromResult("prompt"),
            resolvePlan: resolvePlan
                ?? ((_, _, _) => Task.FromResult(PlanResult.Success(IterationPlan.Default()))),
            resolveRepositories: _ => [],
            syncAgents: _ => Task.CompletedTask,
            generateMergeCommitMessage: (_, _) => Task.FromResult("message"),
            logger: driverLogger);

        var logger = new HpwCapturingLogger<TaskCompletionService>();
        var completionService = new TaskCompletionService(
            pipelineManager,
            brain: new HpwFakeBrain(),
            pipelineDriver: driver,
            lifecycleService: lifecycleService,
            dashboardNotifier: null,
            logger: logger);

        return new Harness(driver, completionService, pipeline, pipelineManager, goal, store, logger, taskId, driverLogger);
    }

    /// <summary>
    /// A re-planning gate: parks inside <c>resolvePlan</c> (exactly where the Brain spends the
    /// minutes-long re-plan) until the test releases it, recording the pipeline phase it observed.
    /// Purely event-driven — no polling, no delays.
    /// </summary>
    private sealed class PlanGate
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once re-planning has actually started (the window is open).</summary>
        internal Task Entered => _entered.Task;

        /// <summary>Pipeline phases observed by planning, in call order.</summary>
        internal List<GoalPhase> ObservedPhases { get; } = [];

        /// <summary>The plan handed back to the driver, so tests can assert the install.</summary>
        internal IterationPlan? InstalledPlan { get; private set; }

        internal void Release() => _release.TrySetResult();

        internal async Task<PlanResult> ResolvePlanAsync(
            GoalPipeline pipeline, string? context, CancellationToken ct)
        {
            lock (ObservedPhases)
            {
                ObservedPhases.Add(pipeline.Phase);
            }

            _entered.TrySetResult();
            await _release.Task.WaitAsync(ct);

            InstalledPlan = new IterationPlan
            {
                Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging],
                Reason = "re-planned after failure",
            };
            return PlanResult.Success(InstalledPlan);
        }
    }

    /// <summary>
    /// Records each log message together with the pipeline phase sampled AT LOG TIME. The driver
    /// logs the "sending back to Coder for rebase" line between the merge-entry handling/budget
    /// checks and the summary build, which makes it an observable seam for pinning the window
    /// transition ahead of those EARLY operations.
    /// </summary>
    internal sealed class HpwPhaseSamplingLogger<T>(Func<GoalPhase> samplePhase) : ILogger<T>
    {
        private readonly List<(string Message, GoalPhase PhaseAtLog)> _entries = [];

        /// <summary>Snapshot of every logged message with the phase observed as it was written.</summary>
        internal IReadOnlyList<(string Message, GoalPhase PhaseAtLog)> Entries
        {
            get { lock (_entries) { return [.. _entries]; } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add((formatter(state, exception), samplePhase()));
            }
        }
    }

    /// <summary>Captures every log entry with its level and fully formatted message.</summary>
    private sealed class HpwCapturingLogger<T> : ILogger<T>
    {
        private readonly List<(LogLevel Level, string Message)> _logs = [];

        /// <summary>Snapshot of everything logged so far.</summary>
        internal IReadOnlyList<(LogLevel Level, string Message)> Logs
        {
            get { lock (_logs) { return [.. _logs]; } }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_logs)
            {
                _logs.Add((logLevel, formatter(state, exception)));
            }
        }
    }

    /// <summary>
    /// In-memory <see cref="IGoalStore"/> that records every status update along with the pipeline's
    /// iteration counter sampled AT CALL TIME. That sample is what proves the summary reorder: a
    /// summary built before <c>IterationBudget.TryConsume()</c> carries the OLD iteration number while
    /// the pipeline already reports the NEW one when the persist call lands.
    /// </summary>
    private sealed class HpwRecordingGoalStore(Goal goal) : IGoalStore
    {
        private readonly List<HpwStatusUpdate> _updates = [];

        /// <summary>Pipeline sampled on each update (set by the harness after creation).</summary>
        internal GoalPipeline? Pipeline { get; set; }

        /// <summary>Snapshot of all recorded updates, in call order.</summary>
        internal IReadOnlyList<HpwStatusUpdate> Updates
        {
            get { lock (_updates) { return [.. _updates]; } }
        }

        public string Name => "honest-window-recording-store";

        public Task<IReadOnlyList<Goal>> GetPendingGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>(goal.Status == GoalStatus.Pending ? [goal] : []);

        /// <summary>
        /// Invoked synchronously inside every status update, BEFORE the update is recorded, with
        /// the pipeline phase sampled at that instant. Lets a test observe the phase at an
        /// operation that runs strictly BEFORE re-planning starts.
        /// </summary>
        internal Action<GoalStatus, GoalPhase>? OnUpdate { get; set; }

        public Task UpdateGoalStatusAsync(
            string goalId, GoalStatus status, GoalUpdateMetadata? metadata = null, CancellationToken ct = default)
        {
            var phaseAtCall = Pipeline?.Phase ?? GoalPhase.Planning;
            OnUpdate?.Invoke(status, phaseAtCall);

            lock (_updates)
            {
                _updates.Add(new HpwStatusUpdate(
                    status, metadata, Pipeline?.Iteration ?? 0, phaseAtCall));
            }

            goal.Status = status;
            if (metadata?.FailureReason is not null)
                goal.FailureReason = metadata.FailureReason;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Goal>> GetAllGoalsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([goal]);

        public Task<Goal?> GetGoalAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult(goalId == goal.Id ? goal : null);

        public Task<Goal> CreateGoalAsync(Goal goalToCreate, CancellationToken ct = default) =>
            Task.FromResult(goalToCreate);

        public Task UpdateGoalAsync(Goal goalToUpdate, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> DeleteGoalAsync(string goalId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> SearchGoalsAsync(
            string query, GoalStatus? statusFilter = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<Goal>> GetGoalsByStatusAsync(GoalStatus status, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task AddIterationAsync(string goalId, IterationSummary summary, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<IterationSummary>> GetIterationsAsync(string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<IterationSummary>>([]);

        public Task<Release> CreateReleaseAsync(Release release, CancellationToken ct = default) =>
            Task.FromResult(release);

        public Task<Release?> GetReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult<Release?>(null);

        public Task<IReadOnlyList<Release>> GetReleasesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Release>>([]);

        public Task UpdateReleaseAsync(Release release, CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateReleaseAsync(string releaseId, ReleaseUpdateData update, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> DeleteReleaseAsync(string releaseId, CancellationToken ct = default) => Task.FromResult(false);

        public Task<IReadOnlyList<Goal>> GetGoalsByReleaseAsync(string releaseId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Goal>>([]);

        public Task<IReadOnlyList<ConversationEntry>> GetPipelineConversationAsync(
            string goalId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationEntry>>([]);

        public Task ResetGoalIterationDataAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<(string GoalId, PersistedClarification Clarification)>> GetAllClarificationsAsync(
            int? limit = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string, PersistedClarification)>>([]);
    }

    /// <summary>A recorded status update plus the pipeline state observed at the moment of the call.</summary>
    /// <param name="Status">Status written to the store.</param>
    /// <param name="Metadata">Metadata written with the update (carries the iteration summary).</param>
    /// <param name="PipelineIterationAtCall">Pipeline iteration counter sampled during the call.</param>
    /// <param name="PipelinePhaseAtCall">Pipeline phase sampled during the call.</param>
    private sealed record HpwStatusUpdate(
        GoalStatus Status,
        GoalUpdateMetadata? Metadata,
        int PipelineIterationAtCall,
        GoalPhase PipelinePhaseAtCall);

    /// <summary>Minimal brain stub: the driver only needs a non-null brain to attempt re-planning.</summary>
    private sealed class HpwFakeBrain : IDistributedBrain
    {
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateModelAsync(
            string model, int? maxContextTokens, Microsoft.Extensions.AI.ReasoningEffort? reasoningEffort,
            CancellationToken ct) => Task.CompletedTask;

        public Task<PlanResult> PlanIterationAsync(
            GoalPipeline pipeline, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PlanResult.Success(IterationPlan.Default()));

        public Task<PromptResult> CraftPromptAsync(
            GoalPipeline pipeline, GoalPhase phase, string? additionalContext = null, CancellationToken ct = default) =>
            Task.FromResult(PromptResult.Success($"Work on {pipeline.Description} as {phase}"));

        public Task<string?> GenerateCommitMessageAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task EnsureBrainRepoAsync(
            string repoName, string repoUrl, string defaultBranch, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectOrchestratorInstructionsAsync(string instructions, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task InjectSystemNoteAsync(GoalPipeline pipeline, string note, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<BrainResponse> AskQuestionAsync(
            string goalId, int iteration, string phase, string workerRole, string question,
            CancellationToken ct = default) =>
            Task.FromResult(BrainResponse.Answer("proceed"));

        public Task ResetSessionAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task ForkSessionForGoalAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteGoalSessionAsync(string goalId, CancellationToken ct = default) => Task.CompletedTask;

        public Task RegisterExistingGoalSessionAsync(string goalId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public bool GoalSessionExists(string goalId) => false;

        public Task<string> SummarizeAndMergeAsync(GoalPipeline pipeline, CancellationToken ct = default) =>
            Task.FromResult($"Goal '{pipeline.GoalId}' completed.");

        public BrainStats? GetStats() => null;
    }
}
