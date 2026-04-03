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
/// Enforces valid pipeline phase transitions via an explicit transition table.
/// The Brain's interpretation is an INPUT to this machine — it decides what phase comes next.
/// <para>Key invariants:</para>
/// <list type="bullet">
///   <item>Done is only reachable when Merging succeeds</item>
///   <item>Failed is reachable from any active state via <see cref="Fail"/></item>
///   <item>New iterations always reset the phase queue (caller calls <see cref="StartIteration"/>)</item>
///   <item>Improve failures are non-blocking (skip to next phase)</item>
/// </list>
/// </summary>
public sealed class PipelineStateMachine
{
    private readonly Queue<GoalPhase> _remainingPhases = new();
    private readonly HashSet<GoalPhase> _completedPhases = [];

    /// <summary>Current phase of the pipeline.</summary>
    public GoalPhase Phase { get; private set; } = GoalPhase.Planning;

    /// <summary>Phases completed in the current iteration.</summary>
    public IReadOnlySet<GoalPhase> CompletedPhases => _completedPhases;

    /// <summary>Remaining phases to execute in the current iteration (read-only snapshot).</summary>
    public IReadOnlyList<GoalPhase> RemainingPhases => [.. _remainingPhases];

    /// <summary>
    /// Restore the state machine mid-iteration from a persisted plan and current phase.
    /// Phases before <paramref name="currentPhase"/> in the plan are marked completed;
    /// the current phase becomes active; phases after it are queued.
    /// </summary>
    public void RestoreFromPlan(IReadOnlyList<GoalPhase> phases, GoalPhase currentPhase)
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

    /// <summary>
    /// Initialize the state machine for a new iteration with the given phase plan.
    /// Resets the phase queue and sets Phase to the first phase (Coding or DocWriting).
    /// </summary>
    /// <param name="phases">Ordered phases for this iteration. Must start with Coding or DocWriting and end with Merging.</param>
    /// <exception cref="ArgumentException">If the plan is empty, doesn't start with Coding or DocWriting, or doesn't end with Merging.</exception>
    public void StartIteration(IReadOnlyList<GoalPhase> phases)
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

    /// <summary>
    /// Process a transition based on the current phase and the given input.
    /// </summary>
    /// <returns>The resulting phase and effect.</returns>
    /// <exception cref="InvalidOperationException">If the transition is invalid for the current state.</exception>
    public TransitionResult Transition(PhaseInput input)
    {
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

    /// <summary>
    /// Force the state machine into the Failed terminal state.
    /// Used when retry/iteration limits are exceeded.
    /// </summary>
    public void Fail()
    {
        Phase = GoalPhase.Failed;
        _remainingPhases.Clear();
    }

    private TransitionResult AdvanceToNext()
    {
        _completedPhases.Add(Phase);
        if (_remainingPhases.Count == 0)
            throw new InvalidOperationException(
                $"No remaining phases after {Phase}. Plan must end with Merging.");

        Phase = _remainingPhases.Dequeue();
        return new(Phase, TransitionEffect.Continue);
    }

    private TransitionResult NewIteration()
    {
        _completedPhases.Clear();
        _remainingPhases.Clear();
        Phase = GoalPhase.Coding;
        return new(GoalPhase.Coding, TransitionEffect.NewIteration);
    }

    private TransitionResult Complete()
    {
        _completedPhases.Add(GoalPhase.Merging);
        _remainingPhases.Clear();
        Phase = GoalPhase.Done;
        return new(GoalPhase.Done, TransitionEffect.Completed);
    }

    private TransitionResult InvalidTransition(PhaseInput input) =>
        throw new InvalidOperationException($"Invalid transition: {Phase} + {input}.");

    /// <summary>
    /// Returns the 1-based occurrence count of the current phase within the executed portion of the plan.
    /// The "executed portion" is: all completed phases + the current phase.
    /// </summary>
    public int GetCurrentPhaseOccurrence(IReadOnlyList<GoalPhase> planPhases)
    {
        // Position in plan = total phases - remaining - 1 (for current phase)
        var currentPosition = planPhases.Count - _remainingPhases.Count - 1;
        var count = 0;
        for (var i = 0; i <= currentPosition && i < planPhases.Count; i++)
        {
            if (planPhases[i] == Phase)
                count++;
        }
        return count > 0 ? count : 1;
    }
}
