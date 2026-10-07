using CopilotHive.Goals;

namespace CopilotHive.Services;

/// <summary>
/// THE SINGLE RESUME-ELIGIBILITY RULE. Both the resume path
/// (<see cref="GoalDispatcher.ResumeGoalAsync"/>) and the dashboard's Goal Detail page read the
/// resume decision from here, so the "Resume" button can never disagree with what the backend
/// would accept.
/// <para>
/// A goal is resumable iff ALL of the following hold:
/// <list type="number">
///   <item><description>the goal is <see cref="GoalStatus.Failed"/> and was NOT cancelled by a
///     user (<see cref="IsCancellationFailure"/> is EQUALITY, never <c>Contains</c>);</description></item>
///   <item><description>its pipeline was found (in memory, else in the store) and its phase is
///     <see cref="GoalPhase.Failed"/> — a MISSING pipeline is never resumable;</description></item>
///   <item><description>when the pipeline carries a <see cref="GoalPipeline.CoderBranch"/> it must
///     equal <c>copilothive/{goalId}</c> ORDINALLY (branch names are case-sensitive, so a
///     case-only difference is a rejection);</description></item>
///   <item><description>when the pipeline carries NO coder branch, the goal must be an
///     iteration-exhaustion failure (<see cref="IsIterationExhaustionFailure"/>), which is the only
///     failure the branchless resume is defined for.</description></item>
/// </list>
/// </para>
/// </summary>
public static class GoalResumeEligibility
{
    /// <summary>
    /// The canonical coder-branch name for <paramref name="goalId"/>. The branch-name invariant
    /// compares against THIS rendering under <see cref="StringComparison.Ordinal"/>.
    /// </summary>
    /// <param name="goalId">The goal identifier.</param>
    /// <returns>The canonical branch name, <c>copilothive/{goalId}</c>.</returns>
    public static string CanonicalCoderBranch(string goalId) => $"copilothive/{goalId}";

    /// <summary>
    /// Determines whether a goal failed specifically due to iteration-budget exhaustion,
    /// making it eligible for the branchless (variant B) resumption via
    /// <see cref="GoalDispatcher.ResumeGoalAsync"/>.
    /// Matches the failure reasons produced by <see cref="PipelineDriver"/>:
    /// "Exceeded max iterations" and "Exceeded max iterations during merge conflict resolution".
    /// A non-<see cref="GoalStatus.Failed"/> goal, a null/empty failure reason and any reason that
    /// does not contain "max iterations" (case-insensitively) are all false.
    /// </summary>
    /// <param name="goal">The goal to inspect.</param>
    /// <returns><c>true</c> iff the goal failed by exhausting the iteration budget.</returns>
    public static bool IsIterationExhaustionFailure(Goal goal)
    {
        if (goal.Status != GoalStatus.Failed)
            return false;
        if (string.IsNullOrEmpty(goal.FailureReason))
            return false;
        var reason = goal.FailureReason;
        return reason.Contains("Exceeded max iterations", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("max iterations", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The cancellation predicate: a goal is cancellation-failed iff its
    /// <see cref="Goal.FailureReason"/> EQUALS "Cancelled by user" under
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>. Equality — never <c>Contains</c>: a
    /// reason such as "Cancelled by user (test)" is a different failure and stays resumable.
    /// Cancellation-failed goals are never resumable (the snapshot-removal contract owns them).
    /// </summary>
    /// <param name="goal">The goal to inspect.</param>
    /// <returns><c>true</c> iff the goal's failure reason IS the cancellation wording.</returns>
    public static bool IsCancellationFailure(Goal goal) =>
        string.Equals(goal.FailureReason, "Cancelled by user", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Goal-level resume eligibility: a Failed goal that was not cancelled by a user.
    /// Deliberately broader than <see cref="IsIterationExhaustionFailure"/> so that any
    /// non-cancellation failure reaches the in-lock pipeline load, where
    /// <see cref="GoalPipeline.CoderBranch"/> decides between the branch-backed restart
    /// (variant A) and the branchless exhaustion resume (variant B).
    /// </summary>
    /// <param name="goal">The goal to inspect.</param>
    /// <returns><c>true</c> iff the goal is a resume candidate at the goal level.</returns>
    public static bool IsResumeCandidateGoal(Goal goal) =>
        goal.Status == GoalStatus.Failed && !IsCancellationFailure(goal);

    /// <summary>
    /// THE SHARED RESUME RULE: whether <see cref="GoalDispatcher.ResumeGoalAsync"/> would accept a
    /// resume for a goal in this exact state.
    /// </summary>
    /// <remarks>
    /// The <paramref name="pipelinePhase"/>/<paramref name="coderBranch"/> pair is whatever the
    /// caller observed — <see cref="GoalPipelineManager.GetPhaseAndCoderBranch"/> for the dashboard,
    /// the loaded/restored pipeline for the dispatcher. A <c>null</c> phase means NO pipeline was
    /// found, which is NOT resumable; a non-null phase must be
    /// <see cref="GoalPhase.Failed"/>. A non-null branch must equal
    /// <see cref="CanonicalCoderBranch"/> ORDINALLY; a null branch requires an
    /// iteration-exhaustion failure reason. Cancellation-failed goals are never resumable, and a
    /// goal whose status is not <see cref="GoalStatus.Failed"/> is never resumable.
    /// </remarks>
    /// <param name="goal">The goal whose resume eligibility is evaluated.</param>
    /// <param name="pipelinePhase">The goal's pipeline phase, or <c>null</c> when no pipeline exists.</param>
    /// <param name="coderBranch">The pipeline's coder branch, or <c>null</c> when it has none.</param>
    /// <returns><c>true</c> iff a resume would be accepted.</returns>
    public static bool IsResumable(Goal goal, GoalPhase? pipelinePhase, string? coderBranch)
    {
        // Rule 1 — Failed and not cancellation-failed.
        if (!IsResumeCandidateGoal(goal))
            return false;

        // Rule 2 — a pipeline must exist AND be in the Failed phase (a missing pipeline is never
        // resumable; the dispatcher restores it from the store before this check).
        if (pipelinePhase != GoalPhase.Failed)
            return false;

        // Rule 3 — a branch, when present, must be the canonical branch, ORDINALLY.
        if (coderBranch is not null)
            return string.Equals(coderBranch, CanonicalCoderBranch(goal.Id), StringComparison.Ordinal);

        // Rule 4 — a branchless resume is only defined for iteration exhaustion.
        return IsIterationExhaustionFailure(goal);
    }
}
