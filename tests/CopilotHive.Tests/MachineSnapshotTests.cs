using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>
/// Contract tests for the <see cref="PipelineStateMachine"/> lock retrofit and the
/// <see cref="PipelineStateMachine.CapturePosition"/> position-snapshot API (slice A0 of the
/// work-slot integrity chain).
/// <para>
/// The machine's honest claim is INTERNAL consistency: every public entry point runs its full
/// operation under one private lock, so a captured (phase, occurrence) PAIR always belongs to a
/// single machine state — never a mixture of a before-state and an after-state. These tests pin
/// that pair-atomicity from two angles: reentrantly (the test seam runs inside the lock on the
/// transitioning thread) and from a genuinely blocked second thread.
/// </para>
/// <para>
/// Every wait in this class is BOUNDED: a regression must surface as a test failure, never as a
/// hang or a deadlock.
/// </para>
/// </summary>
public sealed class MachineSnapshotTests
{
    /// <summary>Generous upper bound for every wait/join — a hang is a failure, not a slow test.</summary>
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Grace window granted to a second thread AFTER it signalled its capture attempt. A correctly
    /// locked <c>CapturePosition</c> cannot complete inside it (the hook still holds the lock); an
    /// UNSYNCHRONIZED one would comfortably finish, which is what makes the blocked-thread proof
    /// discriminating rather than vacuous.
    /// </summary>
    private static readonly TimeSpan BlockedGrace = TimeSpan.FromMilliseconds(150);

    /// <summary>A repeated-phase plan: Coding appears twice, so occurrence and phase move together.</summary>
    private static readonly List<GoalPhase> RepeatedCodingPlan =
        [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Review, GoalPhase.Merging];

    private static PipelineStateMachine Started(IReadOnlyList<GoalPhase> plan)
    {
        var sm = new PipelineStateMachine();
        sm.StartIteration(plan);
        return sm;
    }

    // ══════════════════════════════════════════════════════════════════════
    //  (a) THE REENTRANT RACE — the pair is never a mixture
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The primary atomicity proof. The test seam fires at the top of <c>Transition</c> while the
    /// machine lock is held, and calls <see cref="PipelineStateMachine.CapturePosition"/>
    /// reentrantly on the SAME thread: it must observe the COMPLETE BEFORE-state pair. Once the
    /// hook returns and the transition completes, a fresh capture must observe the COMPLETE
    /// AFTER-state pair.
    /// <para>
    /// The vehicle is a repeated-phase plan, so the before/after pairs differ in BOTH fields —
    /// (Testing, 1) → (Coding, 2). Any mixture — (Testing, 2) or (Coding, 1) — is therefore
    /// directly observable, and (Coding, 1) is itself a real earlier machine state.
    /// </para>
    /// </summary>
    [Fact]
    public void CapturePosition_ReentrantlyDuringTransition_SeesCompleteBeforeState_ThenCompleteAfterState()
    {
        var sm = Started(RepeatedCodingPlan);
        sm.Transition(PhaseInput.Succeeded); // Coding → Testing

        MachinePositionSnapshot? duringHook = null;
        sm.OnTransitionForTest = () => duringHook = sm.CapturePosition(RepeatedCodingPlan);

        sm.Transition(PhaseInput.Succeeded); // Testing → Coding (SECOND occurrence)
        sm.OnTransitionForTest = null;

        var after = sm.CapturePosition(RepeatedCodingPlan);

        // COMPLETE before-state: both fields from the pre-transition machine.
        Assert.NotNull(duringHook);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Testing, 1, OccurrenceFound: true), duringHook);

        // COMPLETE after-state: both fields from the post-transition machine.
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 2, OccurrenceFound: true), after);

        // NO MIXTURE of the two states, spelled out.
        Assert.NotEqual(new MachinePositionSnapshot(GoalPhase.Testing, 2, OccurrenceFound: true), duringHook);
        Assert.NotEqual(new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: true), duringHook);
        Assert.NotEqual(new MachinePositionSnapshot(GoalPhase.Testing, 2, OccurrenceFound: true), after);
        Assert.NotEqual(new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: true), after);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  (b) THE BLOCKED-THREAD PROOF — deadlock-safe recipe
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A second thread signals its capture ATTEMPT immediately before calling
    /// <see cref="PipelineStateMachine.CapturePosition"/>, then parks on the machine lock which
    /// the in-hook transition still holds. The hook waits on that ATTEMPT signal only — never on
    /// the capture's completion, which is what keeps the recipe deadlock-free — and then samples
    /// whether a result has appeared.
    /// <para>Only the two achievable facts are asserted:</para>
    /// <list type="number">
    ///   <item>the second thread was BLOCKED while the hook held the lock (its attempt had
    ///     started, yet no result was available at the hook's observation point);</item>
    ///   <item>once unblocked, its result is the COMPLETE AFTER-state — never a mixture.</item>
    /// </list>
    /// <para>
    /// Nothing is asserted about caller-observable completion ORDERING after the lock is released:
    /// that is not a fact the lock provides.
    /// </para>
    /// </summary>
    [Fact]
    public void CapturePosition_FromSecondThread_BlocksDuringTransition_ThenReturnsCompleteAfterState()
    {
        var sm = Started(RepeatedCodingPlan);
        sm.Transition(PhaseInput.Succeeded); // Coding → Testing

        using var captureAttempted = new ManualResetEventSlim(false);
        MachinePositionSnapshot? secondThreadResult = null;
        MachinePositionSnapshot? resultVisibleToHook = null;
        var hookObservedAttempt = false;

        var reader = new Thread(() =>
        {
            captureAttempted.Set();                        // signalled IMMEDIATELY BEFORE the call…
            var captured = sm.CapturePosition(RepeatedCodingPlan);  // …which parks on the machine lock
            Volatile.Write(ref secondThreadResult, captured);
        })
        {
            IsBackground = true,
            Name = "machine-snapshot-blocked-reader",
        };

        sm.OnTransitionForTest = () =>
        {
            // The reader is STARTED from inside the hook, so the machine lock is provably already
            // held when it makes its attempt — there is no start-order race to lose.
            reader.Start();

            // Bounded wait on the ATTEMPT signal only. Waiting on the completion here would be a
            // self-inflicted deadlock — the hook is the very thing holding the lock.
            hookObservedAttempt = captureAttempted.Wait(WaitTimeout);
            Thread.Sleep(BlockedGrace);
            resultVisibleToHook = Volatile.Read(ref secondThreadResult);
        };

        sm.Transition(PhaseInput.Succeeded); // Testing → Coding (second occurrence)
        sm.OnTransitionForTest = null;

        Assert.True(reader.Join(WaitTimeout), "The blocked capture never completed within the timeout.");

        // (i) BLOCKED: the attempt had begun, but no result existed while the hook held the lock.
        Assert.True(hookObservedAttempt, "The second thread never signalled its capture attempt.");
        Assert.Null(resultVisibleToHook);

        // (ii) Once unblocked: the COMPLETE after-state, never a mixture.
        Assert.Equal(
            new MachinePositionSnapshot(GoalPhase.Coding, 2, OccurrenceFound: true),
            secondThreadResult);
        Assert.NotEqual(
            new MachinePositionSnapshot(GoalPhase.Testing, 2, OccurrenceFound: true),
            secondThreadResult);
        Assert.NotEqual(
            new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: true),
            secondThreadResult);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  (c) THE THREE PLAN CASES
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>No plan (null or empty) yields the current phase with occurrence 0 and no find.</summary>
    [Fact]
    public void CapturePosition_WithNullOrEmptyPlan_ReturnsZeroOccurrenceAndNotFound()
    {
        var sm = Started([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging]);

        var fromNull = sm.CapturePosition(null);
        var fromEmpty = sm.CapturePosition([]);

        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 0, OccurrenceFound: false), fromNull);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 0, OccurrenceFound: false), fromEmpty);
    }

    /// <summary>
    /// A plan whose executed prefix does NOT contain the current phase yields the defaulted
    /// occurrence 1 with the honest <c>OccurrenceFound == false</c> — the flag is what
    /// distinguishes "found at occurrence 1" from "not found, defaulted to 1".
    /// </summary>
    [Fact]
    public void CapturePosition_PhaseNotInPlanPrefix_ReturnsOccurrenceOneAndNotFound()
    {
        var sm = Started([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging]);
        List<GoalPhase> foreignPlan = [GoalPhase.DocWriting, GoalPhase.Review, GoalPhase.Merging];

        var snapshot = sm.CapturePosition(foreignPlan);

        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: false), snapshot);
        Assert.False(snapshot.OccurrenceFound);
        Assert.Equal(1, snapshot.Occurrence);
    }

    /// <summary>A phase present in the executed prefix yields its occurrence with the flag set.</summary>
    [Fact]
    public void CapturePosition_PhaseFoundInPlanPrefix_ReturnsOccurrenceAndFound()
    {
        List<GoalPhase> plan = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging];
        var sm = Started(plan);
        sm.Transition(PhaseInput.Succeeded); // → Testing

        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Testing, 1, OccurrenceFound: true), sm.CapturePosition(plan));

        sm.Transition(PhaseInput.Succeeded); // → Review
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Review, 1, OccurrenceFound: true), sm.CapturePosition(plan));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  (d) THE FORMULA — executed-prefix occurrences of a repeated phase
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The executed prefix is derived from the queue length
    /// (<c>plan.Count - remaining - 1</c>), so a repeated phase counts its occurrences only up to
    /// the CURRENT position: the first Coding is occurrence 1, the second is occurrence 2.
    /// </summary>
    [Fact]
    public void CapturePosition_RepeatedPhase_CountsExecutedPrefixOccurrences()
    {
        var sm = Started(RepeatedCodingPlan);

        // Position 0 — only the first Coding is in the executed prefix.
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: true),
            sm.CapturePosition(RepeatedCodingPlan));

        sm.Transition(PhaseInput.Succeeded); // → Testing (position 1)
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Testing, 1, OccurrenceFound: true),
            sm.CapturePosition(RepeatedCodingPlan));

        sm.Transition(PhaseInput.Succeeded); // → Coding, SECOND occurrence (position 2)
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 2, OccurrenceFound: true),
            sm.CapturePosition(RepeatedCodingPlan));

        sm.Transition(PhaseInput.Succeeded); // → Review (position 3)
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Review, 1, OccurrenceFound: true),
            sm.CapturePosition(RepeatedCodingPlan));

        sm.Transition(PhaseInput.Succeeded); // → Merging (position 4)
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Merging, 1, OccurrenceFound: true),
            sm.CapturePosition(RepeatedCodingPlan));
    }

    /// <summary>
    /// The executed prefix STOPS at the current position — it must not run one entry too far.
    /// With CONSECUTIVE repeats (<c>[Coding, Testing, Testing, Merging]</c>) the machine sitting on
    /// the FIRST Testing must report occurrence 1: the second Testing is still queued, so an
    /// off-by-one in <c>plan.Count - remaining - 1</c> would over-count it as occurrence 2.
    /// </summary>
    [Fact]
    public void CapturePosition_ConsecutiveRepeats_StopsAtCurrentPosition_NoOffByOne()
    {
        List<GoalPhase> plan = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Testing, GoalPhase.Merging];
        var sm = Started(plan);

        sm.Transition(PhaseInput.Succeeded); // → FIRST Testing (position 1); the second is queued
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Testing, 1, OccurrenceFound: true), sm.CapturePosition(plan));

        sm.Transition(PhaseInput.Succeeded); // → SECOND Testing (position 2)
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Testing, 2, OccurrenceFound: true), sm.CapturePosition(plan));
    }

    /// <summary>
    /// The occurrence and the flag agree with the long-standing
    /// <see cref="PipelineStateMachine.GetCurrentPhaseOccurrence"/> whenever the phase IS found —
    /// the snapshot reuses that formula rather than inventing a new one.
    /// </summary>
    [Fact]
    public void CapturePosition_Occurrence_MatchesGetCurrentPhaseOccurrence_WhenFound()
    {
        var sm = Started(RepeatedCodingPlan);
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → Coding (second)

        var snapshot = sm.CapturePosition(RepeatedCodingPlan);

        Assert.True(snapshot.OccurrenceFound);
        Assert.Equal(sm.GetCurrentPhaseOccurrence(RepeatedCodingPlan), snapshot.Occurrence);
        Assert.Equal(2, snapshot.Occurrence);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  (e) HONEST FLAG SEMANTICS — the reordered-plan caveat
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// KNOWN LIMITATION, pinned deliberately. <c>OccurrenceFound</c> is the phase-presence walk's
    /// result, NOT a proof that the supplied plan matches the machine's queue. Here the plan is a
    /// REORDERING of the executed sequence (the machine really ran Coding → Testing, while the
    /// plan claims Testing → Coding): the walk still finds Testing in the executed prefix, so the
    /// flag is <c>true</c> even though the plan does not describe what actually happened.
    /// </summary>
    [Fact]
    public void CapturePosition_ReorderedPlanThatDoesNotMatchTheQueue_StillReportsFound_KnownLimitation()
    {
        List<GoalPhase> actualPlan = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging];
        var sm = Started(actualPlan);
        sm.Transition(PhaseInput.Succeeded); // actually executed: Coding, then Testing

        // A plan that claims a DIFFERENT order than the machine actually executed.
        List<GoalPhase> reorderedPlan = [GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Review, GoalPhase.Merging];

        var snapshot = sm.CapturePosition(reorderedPlan);

        Assert.Equal(GoalPhase.Testing, snapshot.Phase);
        Assert.True(snapshot.OccurrenceFound,
            "OccurrenceFound is a presence signal over the executed prefix, not a plan/queue-agreement proof.");
        Assert.Equal(1, snapshot.Occurrence);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  (f) LOCK-RETROFIT PROOFS
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <c>CompletedPhases</c> now returns a point-in-time DETACHED copy: mutating the returned set
    /// does not reach the machine, and later machine mutations do not reach an earlier snapshot.
    /// </summary>
    [Fact]
    public void CompletedPhases_ReturnsDetachedCopy_MutationsDoNotReachTheMachine()
    {
        var sm = Started([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging]);
        sm.Transition(PhaseInput.Succeeded); // Coding completed

        var snapshot = sm.CompletedPhases;
        Assert.Equal([GoalPhase.Coding], snapshot);

        // Mutating the returned set must NOT reach the machine.
        var mutable = Assert.IsType<HashSet<GoalPhase>>(snapshot);
        mutable.Add(GoalPhase.Improve);
        Assert.DoesNotContain(GoalPhase.Improve, sm.CompletedPhases);

        // …and a later machine mutation must NOT reach the earlier snapshot.
        sm.Transition(PhaseInput.Succeeded); // Testing completed
        Assert.DoesNotContain(GoalPhase.Testing, snapshot);
        Assert.Contains(GoalPhase.Testing, sm.CompletedPhases);

        // Successive reads are distinct instances — nothing hands out the live view.
        Assert.NotSame(sm.CompletedPhases, sm.CompletedPhases);
    }

    /// <summary>
    /// <c>RemainingPhases</c> read from a second thread that is parked on the machine lock during
    /// a transition yields a COHERENT copy — one of the two valid queue states, never a torn or
    /// half-dequeued view.
    /// </summary>
    [Fact]
    public void RemainingPhases_ReadDuringConcurrentTransition_IsCoherent()
    {
        var sm = Started(RepeatedCodingPlan);
        sm.Transition(PhaseInput.Succeeded); // → Testing; queue: Coding, Review, Merging

        GoalPhase[] before = [GoalPhase.Coding, GoalPhase.Review, GoalPhase.Merging];
        GoalPhase[] after = [GoalPhase.Review, GoalPhase.Merging];

        using var readAttempted = new ManualResetEventSlim(false);
        IReadOnlyList<GoalPhase>? observed = null;

        var reader = new Thread(() =>
        {
            readAttempted.Set();
            var copy = sm.RemainingPhases;
            Volatile.Write(ref observed, copy);
        })
        {
            IsBackground = true,
            Name = "machine-snapshot-remaining-reader",
        };

        sm.OnTransitionForTest = () =>
        {
            // Started from inside the hook so the read provably races an in-flight transition.
            reader.Start();
            Assert.True(readAttempted.Wait(WaitTimeout));
        };

        sm.Transition(PhaseInput.Succeeded); // Testing → Coding
        sm.OnTransitionForTest = null;

        Assert.True(reader.Join(WaitTimeout), "The concurrent RemainingPhases read never completed.");
        Assert.NotNull(observed);
        Assert.True(
            observed.SequenceEqual(before) || observed.SequenceEqual(after),
            $"Torn RemainingPhases view: [{string.Join(", ", observed)}]");

        Assert.Equal(after, sm.RemainingPhases);
    }

    /// <summary>
    /// <c>GetCurrentPhaseOccurrence</c> honours the stable-input covenant: it enumerates the very
    /// list instance the caller supplied (no copy, no input synchronization), so a caller-side
    /// change to that list is reflected by the next call.
    /// </summary>
    [Fact]
    public void GetCurrentPhaseOccurrence_UsesTheCallerOwnedListInstance_NoInputCopy()
    {
        var callerPlan = new List<GoalPhase>(RepeatedCodingPlan);
        var sm = Started(callerPlan);
        sm.Transition(PhaseInput.Succeeded); // → Testing
        sm.Transition(PhaseInput.Succeeded); // → Coding (second occurrence)

        // The same caller-owned instance drives both APIs, and they agree.
        Assert.Equal(2, sm.GetCurrentPhaseOccurrence(callerPlan));
        Assert.Equal(2, sm.CapturePosition(callerPlan).Occurrence);

        // NO INPUT COPY: rewriting the caller's list changes the very next answer.
        callerPlan[0] = GoalPhase.DocWriting;
        Assert.Equal(1, sm.GetCurrentPhaseOccurrence(callerPlan));
        Assert.Equal(1, sm.CapturePosition(callerPlan).Occurrence);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  (g) RAW Phase READS — unchanged by the retrofit
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The raw <c>Phase</c> property is untouched API-wise: plain reads still report the machine's
    /// current phase through a full flow (the existing consumers pin this too).
    /// </summary>
    [Fact]
    public void Phase_RawReads_AreUnchangedByTheLockRetrofit()
    {
        var sm = new PipelineStateMachine();
        Assert.Equal(GoalPhase.Planning, sm.Phase);

        sm.StartIteration([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging]);
        Assert.Equal(GoalPhase.Coding, sm.Phase);

        sm.Transition(PhaseInput.Succeeded);
        Assert.Equal(GoalPhase.Testing, sm.Phase);

        sm.Transition(PhaseInput.Failed);
        Assert.Equal(GoalPhase.Planning, sm.Phase);

        sm.Fail();
        Assert.Equal(GoalPhase.Failed, sm.Phase);

        sm.ResetToPlanning();
        Assert.Equal(GoalPhase.Planning, sm.Phase);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  (h) THE RESTORED BASELINE — every field, exactly
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// The repeated-phase restoration baseline, pinned field by field. Restoring
    /// <c>[Coding, Testing, Coding, Merging]</c> at <c>Coding</c> takes the MATCHING branch for
    /// BOTH Coding entries, so neither is queued and the completed set REMAINS EMPTY; the queue is
    /// <c>[Testing, Merging]</c> and the capture over that same plan reports occurrence 1.
    /// This is existing behavior preserved EXACTLY — its correction belongs to slice B.
    /// </summary>
    [Fact]
    public void RestoreFromPlan_RepeatedCurrentPhase_BaselineIsPreservedExactly()
    {
        List<GoalPhase> plan = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Merging];
        var sm = new PipelineStateMachine();

        sm.RestoreFromPlan(plan, GoalPhase.Coding);

        Assert.Equal(GoalPhase.Coding, sm.Phase);
        Assert.Equal([GoalPhase.Testing, GoalPhase.Merging], sm.RemainingPhases);
        Assert.Empty(sm.CompletedPhases);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: true), sm.CapturePosition(plan));
    }

    // ══════════════════════════════════════════════════════════════════════
    //  (i) THE SEAM'S INVOCATION POINT — top of Transition, under the lock
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// ORDERING PROOF: the seam fires at the TOP of <c>Transition</c>, BEFORE the terminal-state
    /// guard rejects the call. A counting hook installed on a Failed machine fires exactly once
    /// even though the transition throws — which is only possible if the seam precedes the guard.
    /// </summary>
    [Fact]
    public void OnTransitionForTest_FiresBeforeTerminalGuardRejection()
    {
        var sm = Started([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging]);
        sm.Fail();
        Assert.Equal(GoalPhase.Failed, sm.Phase);

        var invocations = 0;
        var phaseAtInvocation = new List<GoalPhase>();
        sm.OnTransitionForTest = () =>
        {
            invocations++;
            phaseAtInvocation.Add(sm.Phase);
        };

        var ex = Assert.Throws<InvalidOperationException>(() => sm.Transition(PhaseInput.Succeeded));
        Assert.Equal("Cannot transition from terminal state Failed.", ex.Message);

        // The guard still rejected — AND the seam had already run.
        Assert.Equal(1, invocations);
        Assert.Equal([GoalPhase.Failed], phaseAtInvocation);
    }

    /// <summary>
    /// The same ordering holds for the Planning backstop: the seam fires before the
    /// "Call StartIteration()" rejection, and it runs for EVERY <c>Transition</c> call.
    /// </summary>
    [Fact]
    public void OnTransitionForTest_FiresBeforePlanningGuard_AndOnEveryCall()
    {
        var sm = new PipelineStateMachine();
        var invocations = 0;
        sm.OnTransitionForTest = () => invocations++;

        Assert.Throws<InvalidOperationException>(() => sm.Transition(PhaseInput.Succeeded));
        Assert.Equal(1, invocations);

        sm.StartIteration([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging]);
        sm.Transition(PhaseInput.Succeeded); // valid transition
        Assert.Equal(2, invocations);

        // Invalid-input rejection from an active phase runs the seam too (top of the method).
        Assert.Throws<InvalidOperationException>(() => sm.Transition(PhaseInput.RequestChanges));
        Assert.Equal(3, invocations);

        sm.OnTransitionForTest = null;
    }

    /// <summary>
    /// The seam observes the machine mid-operation with the lock held, so a reentrant capture
    /// taken from a Failed machine still yields a coherent pair before the guard throws.
    /// </summary>
    [Fact]
    public void OnTransitionForTest_ReentrantCaptureFromTerminalState_IsCoherent()
    {
        List<GoalPhase> plan = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging];
        var sm = Started(plan);
        sm.Fail();

        MachinePositionSnapshot? captured = null;
        sm.OnTransitionForTest = () => captured = sm.CapturePosition(plan);

        Assert.Throws<InvalidOperationException>(() => sm.Transition(PhaseInput.Succeeded));
        sm.OnTransitionForTest = null;

        Assert.NotNull(captured);
        Assert.Equal(GoalPhase.Failed, captured.Phase);
        Assert.False(captured.OccurrenceFound);   // Failed is not part of the plan
        Assert.Equal(1, captured.Occurrence);
    }

    // ══════════════════════════════════════════════════════════════════════
    //  (j) SLICE B — RestoreFromPlanAtOccurrence
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>The slice-B repeated-phase plan used by the occurrence-aware vectors.</summary>
    private static readonly List<GoalPhase> SliceBPlan =
        [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Merging];

    /// <summary>
    /// THE A0 BASELINE UNTOUCHED: the legacy repeated-phase restoration is pinned by
    /// <see cref="RestoreFromPlan_RepeatedCurrentPhase_BaselineIsPreservedExactly"/> above and
    /// must stay green and unmodified — slice B only ADDS the occurrence-aware method.
    /// </summary>
    [Fact]
    public void SliceB_LegacyRestoreFromPlanBaseline_RemainsUnmodified()
    {
        // Exactly the A0 baseline vector, re-run here so a regression in RestoreFromPlan
        // surfaces in the slice-B suite too.
        List<GoalPhase> plan = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Merging];
        var sm = new PipelineStateMachine();

        sm.RestoreFromPlan(plan, GoalPhase.Coding);

        Assert.Equal(GoalPhase.Coding, sm.Phase);
        Assert.Equal([GoalPhase.Testing, GoalPhase.Merging], sm.RemainingPhases);
        Assert.Empty(sm.CompletedPhases);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: true), sm.CapturePosition(plan));
    }

    /// <summary>Occurrence 1 on the repeated-phase plan: the FIRST Coding is current.</summary>
    [Fact]
    public void RestoreFromPlanAtOccurrence_OccurrenceOne_RestoresAtFirstMatch()
    {
        var sm = new PipelineStateMachine();

        sm.RestoreFromPlanAtOccurrence(SliceBPlan, GoalPhase.Coding, 1);

        Assert.Equal(GoalPhase.Coding, sm.Phase);
        Assert.Equal([GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Merging], sm.RemainingPhases);
        Assert.Empty(sm.CompletedPhases);
        // THE SELF-CONSISTENCY INVARIANT.
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: true), sm.CapturePosition(SliceBPlan));
    }

    /// <summary>
    /// Occurrence 2 — THE FIX'S PROOF: the second Coding is current, the first Coding and
    /// Testing are completed, and the tail [Merging] survives the restore.
    /// </summary>
    [Fact]
    public void RestoreFromPlanAtOccurrence_OccurrenceTwo_RestoresAtSecondMatch_TailPreserved()
    {
        var sm = new PipelineStateMachine();

        sm.RestoreFromPlanAtOccurrence(SliceBPlan, GoalPhase.Coding, 2);

        Assert.Equal(GoalPhase.Coding, sm.Phase);
        Assert.Equal([GoalPhase.Merging], sm.RemainingPhases);
        Assert.Equal([GoalPhase.Coding, GoalPhase.Testing], sm.CompletedPhases);
        // THE SELF-CONSISTENCY INVARIANT — the pair round-trips: (Coding, 2, true).
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 2, OccurrenceFound: true), sm.CapturePosition(SliceBPlan));
    }

    /// <summary>An over-count (more requested than exist) clamps to the LAST match.</summary>
    [Fact]
    public void RestoreFromPlanAtOccurrence_OverCount_ClampsToLastMatch()
    {
        var sm = new PipelineStateMachine();

        // Only 2 Codings exist; requesting 5 clamps to the LAST one.
        sm.RestoreFromPlanAtOccurrence(SliceBPlan, GoalPhase.Coding, 5);

        Assert.Equal(GoalPhase.Coding, sm.Phase);
        Assert.Equal([GoalPhase.Merging], sm.RemainingPhases);
        Assert.Equal([GoalPhase.Coding, GoalPhase.Testing], sm.CompletedPhases);
        // The clamp restores at the same position as occurrence 2 — the self-consistency
        // invariant holds at the clamped position too.
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 2, OccurrenceFound: true), sm.CapturePosition(SliceBPlan));
    }

    /// <summary>Occurrence ≤ 0 is treated as 1.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void RestoreFromPlanAtOccurrence_NonPositiveOccurrence_TreatedAsOne(int occurrence)
    {
        var sm = new PipelineStateMachine();

        sm.RestoreFromPlanAtOccurrence(SliceBPlan, GoalPhase.Coding, occurrence);

        Assert.Equal(GoalPhase.Coding, sm.Phase);
        Assert.Equal([GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Merging], sm.RemainingPhases);
        Assert.Empty(sm.CompletedPhases);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, 1, OccurrenceFound: true), sm.CapturePosition(SliceBPlan));
    }

    /// <summary>
    /// NO-MATCH LEGACY PATH: when the requested phase is not in the plan at all,
    /// every entry goes to the completed set, the queue is empty, and Phase is the requested
    /// phase — byte-identical to RestoreFromPlan's no-match behavior.
    /// </summary>
    [Fact]
    public void RestoreFromPlanAtOccurrence_NoMatch_LegacyNoFoundPath()
    {
        List<GoalPhase> plan = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging];
        var sm = new PipelineStateMachine();

        sm.RestoreFromPlanAtOccurrence(plan, GoalPhase.Review, 1);

        Assert.Equal(GoalPhase.Review, sm.Phase);
        Assert.Empty(sm.RemainingPhases);
        Assert.Equal([GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging], sm.CompletedPhases);

        // Byte-identical to the legacy method's no-match behavior.
        var legacy = new PipelineStateMachine();
        legacy.RestoreFromPlan(plan, GoalPhase.Review);
        Assert.Equal(legacy.Phase, sm.Phase);
        Assert.Equal(legacy.RemainingPhases, sm.RemainingPhases);
        Assert.Equal(legacy.CompletedPhases, sm.CompletedPhases);
        // And the capture over a plan that does not contain the phase is identical too.
        Assert.Equal(legacy.CapturePosition(plan), sm.CapturePosition(plan));
    }

    /// <summary>
    /// THE SELF-CONSISTENCY INVARIANT, exercised across EVERY vector above: a restore at the
    /// n-th existing occurrence must make CapturePosition report exactly that pair.
    /// Runs the full plan sweep occurrence-by-occurrence.
    /// </summary>
    [Fact]
    public void RestoreFromPlanAtOccurrence_SelfConsistency_InvariantHoldsForEveryMatchPosition()
    {
        // For each of the two Coding occurrences in the plan, restoring at n and capturing
        // must round-trip to (Coding, n, true).
        for (var n = 1; n <= 2; n++)
        {
            var sm = new PipelineStateMachine();
            sm.RestoreFromPlanAtOccurrence(SliceBPlan, GoalPhase.Coding, n);

            var captured = sm.CapturePosition(SliceBPlan);
            Assert.Equal(new MachinePositionSnapshot(GoalPhase.Coding, n, OccurrenceFound: true), captured);
        }

        // The same invariant holds for a NON-repeated phase position (Testing, occurrence 1).
        var smTesting = new PipelineStateMachine();
        smTesting.RestoreFromPlanAtOccurrence(SliceBPlan, GoalPhase.Testing, 1);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Testing, 1, OccurrenceFound: true),
            smTesting.CapturePosition(SliceBPlan));

        // And for the final Merging entry: restore at its occurrence → nothing remains queued.
        var smMerging = new PipelineStateMachine();
        smMerging.RestoreFromPlanAtOccurrence(SliceBPlan, GoalPhase.Merging, 1);
        Assert.Equal(new MachinePositionSnapshot(GoalPhase.Merging, 1, OccurrenceFound: true),
            smMerging.CapturePosition(SliceBPlan));
        Assert.Empty(smMerging.RemainingPhases);
    }
}
