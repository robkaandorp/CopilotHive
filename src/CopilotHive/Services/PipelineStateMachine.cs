using System.Diagnostics;

namespace CopilotHive.Services;

/// <summary>
/// Input signals that drive pipeline state machine transitions.
/// </summary>
public enum PhaseInput
{
    /// <summary>The current phase completed successfully (PASS, APPROVE).</summary>
    Succeeded,

    /// <summary>The current phase failed (FAIL, test failures).</summary>
    Failed,

    /// <summary>The reviewer or doc-writer requested changes.</summary>
    RequestChanges,
}

/// <summary>
/// Effect of a state machine transition on the pipeline.
/// </summary>
public enum TransitionEffect
{
    /// <summary>Advanced to the next phase in the current iteration's plan.</summary>
    Continue,

    /// <summary>A new iteration is needed. Caller must check limits, re-plan, and call StartIteration().</summary>
    NewIteration,

    /// <summary>The goal completed successfully (Merging phase succeeded).</summary>
    Completed,
}

/// <summary>
/// Result of a state machine transition.
/// </summary>
/// <param name="NextPhase">The phase the pipeline transitioned to.</param>
/// <param name="Effect">What happened as a result of the transition.</param>
public record TransitionResult(GoalPhase NextPhase, TransitionEffect Effect);

/// <summary>
/// A point-in-time, internally consistent view of the machine's position: the current phase
/// together with the occurrence of that phase within the executed portion of a supplied plan.
/// Produced by <see cref="PipelineStateMachine.CapturePosition"/> under the machine lock, so the
/// two values can never be a mixture of a before-state and an after-state.
/// </summary>
/// <param name="Phase">The machine's current phase at capture time.</param>
/// <param name="Occurrence">
/// The 1-based occurrence of <paramref name="Phase"/> within the executed prefix of the supplied
/// plan; <c>0</c> when no plan was supplied (null/empty), and <c>1</c> when a plan was supplied
/// but the phase was not found in its executed prefix.
/// </param>
/// <param name="OccurrenceFound">
/// Whether the phase-presence walk actually located <paramref name="Phase"/> in the executed
/// prefix of the supplied plan. See <see cref="PipelineStateMachine.CapturePosition"/> for the
/// honest semantics of this flag.
/// </param>
internal sealed record MachinePositionSnapshot(GoalPhase Phase, int Occurrence, bool OccurrenceFound);

/// <summary>
/// Enforces valid pipeline phase transitions via an explicit transition table.
/// The Brain's interpretation is an INPUT to this machine — it decides what phase comes next.
/// <para>Key invariants:</para>
/// <list type="bullet">
///   <item>Done is only reachable when Merging succeeds</item>
///   <item>Failed is reachable from any active state via <see cref="Fail"/></item>
///   <item>New iterations always reset the phase queue (caller calls <see cref="StartIteration"/>)</item>
///   <item>Improve failures are non-blocking (skip to next phase)</item>
/// </list>
/// <para>
/// SYNCHRONIZATION (the narrowed, honest claim). Every public entry point runs its FULL operation
/// under a private machine lock, so the machine's INTERNAL consistency is guaranteed: each mutation
/// is atomic with respect to every other machine operation, and <see cref="CapturePosition"/>
/// returns a phase/occurrence PAIR that always belongs to one single machine state — never a
/// mixture of a before-state and an after-state.
/// </para>
/// <para>
/// What is NOT claimed: an external direct read of <see cref="Phase"/> observes a coherent single
/// value, but establishes no happens-before edge to the work queue or to any other subsystem. That
/// is exactly the pre-existing visibility class of the property — unchanged by the lock retrofit.
/// Callers that need a synchronized pair must use <see cref="CapturePosition"/>.
/// </para>
/// </summary>
public sealed class PipelineStateMachine
{
    private readonly Queue<GoalPhase> _remainingPhases = new();
    private readonly HashSet<GoalPhase> _completedPhases = [];

    /// <summary>
    /// Guards the machine's mutable state. Held for the FULL body of every public entry point;
    /// the private mutating helpers REQUIRE it to be held and never acquire it themselves.
    /// </summary>
    private readonly object _machineLock = new();

    /// <summary>
    /// Current phase of the pipeline.
    /// <para>
    /// Reads are raw (unsynchronized) — the API is unchanged. Writes now happen under the machine
    /// lock, so a reader sees a coherent single value, but the read carries NO happens-before edge
    /// to the queue or to any other state: it is the pre-existing visibility class, unchanged.
    /// For a synchronized phase/occurrence pair use <see cref="CapturePosition"/>.
    /// </para>
    /// </summary>
    public GoalPhase Phase { get; private set; } = GoalPhase.Planning;

    /// <summary>
    /// Phases completed in the current iteration, as a point-in-time defensive snapshot taken
    /// under the machine lock. The returned set is DETACHED from the machine: later machine
    /// mutations do not change it, and mutating it does not change the machine.
    /// </summary>
    public IReadOnlySet<GoalPhase> CompletedPhases
    {
        get
        {
            lock (_machineLock)
            {
                return new HashSet<GoalPhase>(_completedPhases);
            }
        }
    }

    /// <summary>
    /// Remaining phases to execute in the current iteration (read-only snapshot).
    /// The defensive copy is produced under the machine lock, so it is always a coherent
    /// point-in-time view — never torn across a concurrent transition.
    /// </summary>
    public IReadOnlyList<GoalPhase> RemainingPhases
    {
        get
        {
            lock (_machineLock)
            {
                return [.. _remainingPhases];
            }
        }
    }

    /// <summary>
    /// TEST-ONLY seam: invoked under the machine lock at the very TOP of
    /// <see cref="Transition"/> — BEFORE the terminal/Planning guards and before the transition
    /// table is evaluated. Lets a test observe or re-enter the machine while the lock is held.
    /// There is no exception containment: anything the hook throws propagates out of
    /// <c>Transition</c>. Tests install non-throwing hooks only.
    /// </summary>
    internal Action? OnTransitionForTest { get; set; }

    /// <summary>
    /// Restore the state machine mid-iteration from a persisted plan and current phase.
    /// Phases before <paramref name="currentPhase"/> in the plan are marked completed;
    /// the current phase becomes active; phases after it are queued.
    /// <para>
    /// REPEATED-PHASE BOUNDARY (existing behavior, preserved exactly). When the plan contains the
    /// current phase MORE THAN ONCE, every matching entry takes the matching branch: BOTH matching
    /// entries are omitted from <c>_remainingPhases</c>, and <c>_completedPhases</c> REMAINS EMPTY
    /// because the matching branch never adds to the completed set (and the <c>!found</c> branch
    /// stops applying after the first match). So for the plan
    /// <c>[Coding, Testing, Coding, Merging]</c> restored at <c>Coding</c>, the queue is
    /// <c>[Testing, Merging]</c>, the completed set is empty, and <see cref="CapturePosition"/>
    /// over that same plan yields <c>(Coding, 1, true)</c> — the executed prefix computed from the
    /// queue length covers only the first entry. Correcting this repeated-phase restoration is
    /// slice B's work; this slice preserves it verbatim.
    /// </para>
    /// </summary>
    public void RestoreFromPlan(IReadOnlyList<GoalPhase> phases, GoalPhase currentPhase)
    {
        lock (_machineLock)
        {
            _remainingPhases.Clear();
            _completedPhases.Clear();

            var found = false;
            foreach (var phase in phases)
            {
                if (phase == currentPhase)
                {
                    Phase = phase;
                    found = true;
                }
                else if (!found)
                {
                    _completedPhases.Add(phase);
                }
                else
                {
                    _remainingPhases.Enqueue(phase);
                }
            }

            if (!found)
                Phase = currentPhase;
        }
    }

    /// <summary>
    /// Occurrence-aware restoration: rebuilds the machine so the <paramref name="occurrence"/>-th
    /// (1-based) entry equal to <paramref name="currentPhase"/> becomes the CURRENT phase —
    /// unlike <see cref="RestoreFromPlan"/>, which collapses ALL matching entries into the
    /// matching branch and loses both prior occurrences and the tail after the last one.
    /// <para>
    /// THE MATCH INDEX. The occurrence-th entry equal to <paramref name="currentPhase"/> is the
    /// match. Over-count (more requested than exist) CLAMPS to the LAST match. No match at all
    /// (phase absent from the plan) takes the LEGACY no-found path — every entry to
    /// <c>_completedPhases</c>, <see cref="Phase"/> = <paramref name="currentPhase"/>, empty
    /// queue — byte-identical to <see cref="RestoreFromPlan"/>'s no-match behavior.
    /// <paramref name="occurrence"/> ≤ 0 is treated as 1.
    /// </para>
    /// <para>
    /// THE SPLIT. Entries BEFORE the match go to <c>_completedPhases</c> (prior occurrences of
    /// the current phase included — they already executed); the match itself becomes the current
    /// phase; entries AFTER it are queued in <c>_remainingPhases</c> (later occurrences included —
    /// the tail never vanishes).
    /// </para>
    /// <para>
    /// SELF-CONSISTENCY INVARIANT: after
    /// <c>RestoreFromPlanAtOccurrence(plan, p, n)</c> where the n-th match exists,
    /// <c>CapturePosition(plan)</c> yields EXACTLY <c>(p, n, true)</c> — tested explicitly.
    /// </para>
    /// </summary>
    /// <param name="phases">The plan to restore against.</param>
    /// <param name="currentPhase">The phase whose occurrence-th entry becomes current.</param>
    /// <param name="occurrence">The 1-based occurrence to restore at; ≤ 0 treated as 1.</param>
    public void RestoreFromPlanAtOccurrence(IReadOnlyList<GoalPhase> phases, GoalPhase currentPhase, int occurrence)
    {
        lock (_machineLock)
        {
            _remainingPhases.Clear();
            _completedPhases.Clear();

            // occurrence ≤ 0 → treated as 1.
            var requested = Math.Max(1, occurrence);

            // THE MATCH INDEX: locate the occurrence-th (1-based) matching entry, clamping an
            // over-count to the LAST match. No match at all → the legacy no-found path.
            var matchIndex = -1;
            var seen = 0;
            for (var i = 0; i < phases.Count; i++)
            {
                if (phases[i] != currentPhase)
                    continue;
                seen++;
                if (seen == requested)
                {
                    matchIndex = i;
                    break;
                }
            }

            if (matchIndex < 0 && seen > 0)
            {
                // Over-count: clamp to the LAST match.
                for (var i = phases.Count - 1; i >= 0; i--)
                {
                    if (phases[i] == currentPhase)
                    {
                        matchIndex = i;
                        break;
                    }
                }
            }

            if (matchIndex < 0)
            {
                // NO-MATCH LEGACY PATH — byte-identical to RestoreFromPlan's !found branch:
                // every entry is completed, the queue is empty, Phase is the requested phase.
                foreach (var phase in phases)
                    _completedPhases.Add(phase);
                Phase = currentPhase;
                return;
            }

            // THE SPLIT: before → completed, match → current, after → queued.
            for (var i = 0; i < phases.Count; i++)
            {
                if (i < matchIndex)
                    _completedPhases.Add(phases[i]);
                else if (i > matchIndex)
                    _remainingPhases.Enqueue(phases[i]);
                else
                    Phase = currentPhase;
            }
        }
    }

    /// <summary>
    /// Initialize the state machine for a new iteration with the given phase plan.
    /// Resets the phase queue and sets Phase to the first phase (Coding or DocWriting).
    /// </summary>
    /// <param name="phases">Ordered phases for this iteration. Must start with Coding or DocWriting and end with Merging.</param>
    /// <exception cref="ArgumentException">If the plan is empty, doesn't start with Coding or DocWriting, or doesn't end with Merging.</exception>
    public void StartIteration(IReadOnlyList<GoalPhase> phases)
    {
        lock (_machineLock)
        {
            ArgumentNullException.ThrowIfNull(phases);
            if (phases.Count == 0)
                throw new ArgumentException("Phase plan must not be empty.", nameof(phases));
            if (phases[0] != GoalPhase.Coding && phases[0] != GoalPhase.DocWriting)
                throw new ArgumentException($"Phase plan must start with Coding or DocWriting, got {phases[0]}.", nameof(phases));
            if (phases[^1] != GoalPhase.Merging)
                throw new ArgumentException($"Phase plan must end with Merging, got {phases[^1]}.", nameof(phases));

            _remainingPhases.Clear();
            _completedPhases.Clear();

            // First phase (Coding or DocWriting) becomes current; rest goes in the queue
            Phase = phases[0];
            for (var i = 1; i < phases.Count; i++)
                _remainingPhases.Enqueue(phases[i]);
        }
    }

    /// <summary>
    /// Process a transition based on the current phase and the given input.
    /// </summary>
    /// <returns>The resulting phase and effect.</returns>
    /// <exception cref="InvalidOperationException">If the transition is invalid for the current state.</exception>
    public TransitionResult Transition(PhaseInput input)
    {
        lock (_machineLock)
        {
            // TEST-ONLY seam, deliberately at the TOP: it fires under the lock before the
            // terminal/Planning guards and before the table is evaluated.
            OnTransitionForTest?.Invoke();

            if (Phase is GoalPhase.Done or GoalPhase.Failed)
                throw new InvalidOperationException($"Cannot transition from terminal state {Phase}.");
            if (Phase == GoalPhase.Planning)
                throw new InvalidOperationException(
                    "Call StartIteration() before transitioning from Planning.");

            return Phase switch
            {
                GoalPhase.Coding => input switch
                {
                    PhaseInput.Succeeded => AdvanceToNext(),
                    PhaseInput.Failed => NewIteration(),
                    _ => InvalidTransition(input),
                },
                GoalPhase.Testing => input switch
                {
                    PhaseInput.Succeeded => AdvanceToNext(),
                    PhaseInput.Failed => NewIteration(),
                    _ => InvalidTransition(input),
                },
                GoalPhase.DocWriting => input switch
                {
                    PhaseInput.Succeeded => AdvanceToNext(),
                    PhaseInput.Failed or PhaseInput.RequestChanges => NewIteration(),
                    _ => InvalidTransition(input),
                },
                GoalPhase.Review => input switch
                {
                    PhaseInput.Succeeded => AdvanceToNext(),
                    PhaseInput.Failed or PhaseInput.RequestChanges => NewIteration(),
                    _ => InvalidTransition(input),
                },
                GoalPhase.Improve => input switch
                {
                    PhaseInput.Succeeded or PhaseInput.Failed => AdvanceToNext(),
                    _ => InvalidTransition(input),
                },
                GoalPhase.Merging => input switch
                {
                    PhaseInput.Succeeded => Complete(),
                    PhaseInput.Failed => NewIteration(),
                    _ => InvalidTransition(input),
                },
                _ => throw new InvalidOperationException($"Unexpected phase: {Phase}"),
            };
        }
    }

    /// <summary>
    /// Force the state machine into the Failed terminal state.
    /// Used when retry/iteration limits are exceeded.
    /// </summary>
    public void Fail()
    {
        lock (_machineLock)
        {
            Phase = GoalPhase.Failed;
            _remainingPhases.Clear();
        }
    }

    /// <summary>
    /// Resets the state machine to the Planning phase with empty phase queues.
    /// Used when resuming a failed goal so a fresh iteration plan can be set.
    /// </summary>
    public void ResetToPlanning()
    {
        lock (_machineLock)
        {
            Phase = GoalPhase.Planning;
            _remainingPhases.Clear();
            _completedPhases.Clear();
        }
    }

    private TransitionResult AdvanceToNext()
    {
        Debug.Assert(Monitor.IsEntered(_machineLock));
        _completedPhases.Add(Phase);
        if (_remainingPhases.Count == 0)
            throw new InvalidOperationException(
                $"No remaining phases after {Phase}. Plan must end with Merging.");

        Phase = _remainingPhases.Dequeue();
        return new(Phase, TransitionEffect.Continue);
    }

    private TransitionResult NewIteration()
    {
        Debug.Assert(Monitor.IsEntered(_machineLock));
        _completedPhases.Clear();
        _remainingPhases.Clear();
        // The re-plan window is honestly Planning: the queue is empty, so Transition() cannot
        // advance anywhere and the Planning guard above rejects any further input until the
        // caller re-plans and calls StartIteration().
        Phase = GoalPhase.Planning;
        return new(GoalPhase.Planning, TransitionEffect.NewIteration);
    }

    private TransitionResult Complete()
    {
        Debug.Assert(Monitor.IsEntered(_machineLock));
        _completedPhases.Add(GoalPhase.Merging);
        _remainingPhases.Clear();
        Phase = GoalPhase.Done;
        return new(GoalPhase.Done, TransitionEffect.Completed);
    }

    private TransitionResult InvalidTransition(PhaseInput input)
    {
        Debug.Assert(Monitor.IsEntered(_machineLock));
        throw new InvalidOperationException($"Invalid transition: {Phase} + {input}.");
    }

    /// <summary>
    /// Returns the 1-based occurrence count of the current phase within the executed portion of the plan.
    /// The "executed portion" is: all completed phases + the current phase.
    /// <para>
    /// STABLE-INPUT COVENANT. The machine's own state is read under the machine lock, but
    /// <paramref name="planPhases"/> is CALLER-OWNED and enumerated in place: no copy is made and
    /// no synchronization is applied to it. The caller must supply a list that is stable for the
    /// duration of the call (an immutable plan, or one no other thread is mutating).
    /// </para>
    /// </summary>
    public int GetCurrentPhaseOccurrence(IReadOnlyList<GoalPhase> planPhases)
    {
        GoalPhase phase;
        int remaining;
        lock (_machineLock)
        {
            phase = Phase;
            remaining = _remainingPhases.Count;
        }

        // Position in plan = total phases - remaining - 1 (for current phase)
        var currentPosition = planPhases.Count - remaining - 1;
        var count = 0;
        for (var i = 0; i <= currentPosition && i < planPhases.Count; i++)
        {
            if (planPhases[i] == phase)
                count++;
        }
        return count > 0 ? count : 1;
    }

    /// <summary>
    /// Captures the machine's position as an internally consistent PAIR: the current phase and
    /// the 1-based occurrence of that phase within the executed prefix of
    /// <paramref name="planPhases"/>. The whole computation runs under the machine lock, so the
    /// returned pair always belongs to a single machine state — never a mixture of a before-state
    /// and an after-state of a concurrent transition.
    /// <para>
    /// (1) INPUT-LIST COVENANT. <paramref name="planPhases"/> is CALLER-OWNED: it is enumerated in
    /// place, no defensive copy is made, and no synchronization is applied to it. The caller must
    /// supply a list that is stable for the duration of the call. Taking a defensive copy of the
    /// plan is A1's rider, not this method's responsibility.
    /// </para>
    /// <para>
    /// (2) HONEST FLAG SEMANTICS. <c>OccurrenceFound</c> reports ONLY the result of the
    /// phase-presence walk over the executed prefix: it is <c>true</c> when the current phase was
    /// found there. It does NOT prove that the supplied plan matches the machine's actual queue.
    /// A REORDERED plan whose executed prefix happens to contain the current phase still returns
    /// <c>true</c> even though the plan does not describe the executed sequence — the flag is a
    /// presence signal, never a plan/queue-agreement proof.
    /// </para>
    /// <para>
    /// (3) DOWNSTREAM CONTRACT (documented for A1, not an A0 criterion). A1's capture maps
    /// <c>OccurrenceFound == false</c> onto its <c>PlanUnavailable</c> marker, applying the phase
    /// classification FIRST and only then consulting this flag.
    /// </para>
    /// </summary>
    /// <param name="planPhases">
    /// The plan to locate the current phase in, or <c>null</c>/empty when no plan is available
    /// (which yields occurrence <c>0</c> with <c>OccurrenceFound == false</c>).
    /// </param>
    /// <returns>The phase/occurrence pair together with the honest presence flag.</returns>
    internal MachinePositionSnapshot CapturePosition(IReadOnlyList<GoalPhase>? planPhases)
    {
        lock (_machineLock)
        {
            if (planPhases is null || planPhases.Count == 0)
                return new(Phase, 0, OccurrenceFound: false);

            // Position in plan = total phases - remaining - 1 (for current phase) — the existing formula.
            var currentPosition = planPhases.Count - _remainingPhases.Count - 1;
            var count = 0;
            for (var i = 0; i <= currentPosition && i < planPhases.Count; i++)
            {
                if (planPhases[i] == Phase)
                    count++;
            }

            return new(Phase, count > 0 ? count : 1, OccurrenceFound: count > 0);
        }
    }
}
