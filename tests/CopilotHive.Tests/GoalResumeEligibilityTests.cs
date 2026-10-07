using CopilotHive.Goals;
using CopilotHive.Services;

namespace CopilotHive.Tests;

/// <summary>
/// THE FULL MATRIX of the ONE shared resume rule,
/// <see cref="GoalResumeEligibility.IsResumable"/>, plus the two predicates it is built from.
/// The page's resume button and <see cref="GoalDispatcher.ResumeGoalAsync"/> both read this rule,
/// so every row here is a statement about BOTH.
/// </summary>
public sealed class GoalResumeEligibilityTests
{
    private const string GoalId = "resume-eligibility";

    private static string CanonicalBranch => GoalResumeEligibility.CanonicalCoderBranch(GoalId);

    private static Goal GoalWith(GoalStatus status, string? reason) => new()
    {
        Id = GoalId,
        Description = "Resume eligibility goal",
        Status = status,
        FailureReason = reason,
    };

    // ── Rule 1: Failed and NOT cancellation-failed ────────────────────────────

    [Theory]
    [InlineData(GoalStatus.Draft)]
    [InlineData(GoalStatus.Pending)]
    [InlineData(GoalStatus.InProgress)]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Cancelled)]
    public void IsResumable_NotFailed_IsNeverResumable(GoalStatus status) =>
        Assert.False(GoalResumeEligibility.IsResumable(
            GoalWith(status, "Exceeded max iterations"), GoalPhase.Failed, CanonicalBranch));

    [Theory]
    [InlineData(GoalStatus.Draft)]
    [InlineData(GoalStatus.Pending)]
    [InlineData(GoalStatus.InProgress)]
    [InlineData(GoalStatus.Completed)]
    [InlineData(GoalStatus.Cancelled)]
    public void IsResumable_NotFailed_IsNeverResumableEvenBranchlessAndExhausted(GoalStatus status) =>
        Assert.False(GoalResumeEligibility.IsResumable(
            GoalWith(status, "Exceeded max iterations"), GoalPhase.Failed, coderBranch: null));

    [Theory]
    [InlineData("Cancelled by user")]
    [InlineData("cancelled by user")]
    [InlineData("CANCELLED BY USER")]
    [InlineData("Cancelled By User")]
    public void IsResumable_ExactCancellationReason_IsNeverResumable(string reason) =>
        Assert.False(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, reason), GoalPhase.Failed, CanonicalBranch));

    [Theory]
    [InlineData("Cancelled by user (test)")]
    [InlineData("Cancelled by")]
    [InlineData("Worker reported: cancelled by user timeout")]
    [InlineData("Cancelled by user.")]
    public void IsResumable_CancellationWordingThatIsNotTheExactReason_IsResumable(string reason) =>
        Assert.True(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, reason), GoalPhase.Failed, CanonicalBranch));

    /// <summary>
    /// The cancellation predicate is EQUALITY, never <c>Contains</c>: the suffixed reason is a
    /// DIFFERENT failure and the row is the isolating test for that boundary — remove the equality
    /// (use <c>Contains</c>) and exactly this row flips to false.
    /// </summary>
    [Fact]
    public void IsResumable_CancellationReasonWithSuffix_IsResumableWhileTheBareWordingIsNot()
    {
        Assert.False(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, "Cancelled by user"), GoalPhase.Failed, CanonicalBranch));
        Assert.True(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, "Cancelled by user (test)"), GoalPhase.Failed, CanonicalBranch));
    }

    // ── Rule 2: the pipeline must exist and be Failed ─────────────────────────

    [Fact]
    public void IsResumable_MissingPipeline_IsNotResumable() =>
        Assert.False(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, "Exceeded max iterations"), pipelinePhase: null, coderBranch: null));

    /// <summary>
    /// A missing pipeline is NOT resumable EVEN WHEN the branch would have been canonical: the
    /// phase is the deciding input, so a caller that lost the pipeline can never be told "true".
    /// </summary>
    [Fact]
    public void IsResumable_MissingPipeline_IsNotResumableEvenWithACanonicalBranch() =>
        Assert.False(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, "Review rejected the changes"), pipelinePhase: null, coderBranch: CanonicalBranch));

    [Theory]
    [InlineData(GoalPhase.Planning)]
    [InlineData(GoalPhase.Coding)]
    [InlineData(GoalPhase.Review)]
    [InlineData(GoalPhase.Testing)]
    [InlineData(GoalPhase.DocWriting)]
    [InlineData(GoalPhase.Improve)]
    [InlineData(GoalPhase.Merging)]
    [InlineData(GoalPhase.Done)]
    public void IsResumable_NonFailedPipelinePhase_IsNotResumable(GoalPhase phase) =>
        Assert.False(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, "Review rejected the changes"), phase, CanonicalBranch));

    [Theory]
    [InlineData(GoalPhase.Planning)]
    [InlineData(GoalPhase.Merging)]
    [InlineData(GoalPhase.Done)]
    public void IsResumable_NonFailedPipelinePhase_IsNotResumableEvenBranchlessAndExhausted(GoalPhase phase) =>
        Assert.False(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, "Exceeded max iterations"), phase, coderBranch: null));

    // ── Rule 3: a present branch must be canonical, ORDINALLY ─────────────────

    [Fact]
    public void IsResumable_CanonicalBranch_IsResumable() =>
        Assert.True(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, "Review rejected the changes"), GoalPhase.Failed, CanonicalBranch));

    [Theory]
    [InlineData("copilothive/some-other-goal")]
    [InlineData("copilothive/resume")]
    [InlineData("feature/resume-eligibility")]
    [InlineData("")]
    public void IsResumable_NonCanonicalBranch_IsNotResumable(string branch) =>
        Assert.False(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, "Review rejected the changes"), GoalPhase.Failed, branch));

    /// <summary>
    /// Git branch names are case-sensitive, so the invariant is ORDINAL: a case-only difference is a
    /// MISMATCH. Each row differs from <c>copilothive/resume-eligibility</c> in letter case only —
    /// switch the comparison to <see cref="StringComparison.OrdinalIgnoreCase"/> and exactly these
    /// rows flip to true.
    /// </summary>
    [Theory]
    [InlineData("COPILOTHIVE/resume-eligibility")]
    [InlineData("CopilotHive/Resume-Eligibility")]
    [InlineData("copilothive/RESUME-ELIGIBILITY")]
    public void IsResumable_CaseOnlyBranchMismatch_IsNotResumable(string branch) =>
        Assert.False(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, "Review rejected the changes"), GoalPhase.Failed, branch));

    // ── Rule 4: a branchless resume requires iteration exhaustion ────────────

    [Theory]
    [InlineData("Exceeded max iterations")]
    [InlineData("Exceeded max iterations during merge conflict resolution")]
    [InlineData("exceeded MAX iterations")]
    public void IsResumable_BranchlessIterationExhaustion_IsResumable(string reason) =>
        Assert.True(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, reason), GoalPhase.Failed, coderBranch: null));

    [Theory]
    [InlineData("Worker failed: build error in Coding")]
    [InlineData("Review rejected the changes")]
    [InlineData("Exceeded max test retries")]
    [InlineData("")]
    [InlineData(null)]
    public void IsResumable_BranchlessOtherFailure_IsNotResumable(string? reason) =>
        Assert.False(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, reason), GoalPhase.Failed, coderBranch: null));

    /// <summary>
    /// The branchless rows are a real boundary, not a restatement: the SAME reason is resumable
    /// branchless (exhaustion) and non-resumable branchless (anything else), while the canonical
    /// branch makes both of them resumable — so neither the branch rule nor the reason rule can be
    /// dropped without flipping a row.
    /// </summary>
    [Fact]
    public void IsResumable_BranchlessReasonBoundary_OnlyExhaustionResumes()
    {
        Assert.True(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, "Exceeded max iterations"), GoalPhase.Failed, coderBranch: null));
        Assert.False(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, "Worker failed: build error in Coding"), GoalPhase.Failed, coderBranch: null));
        // The canonical branch short-circuits the reason rule for BOTH reasons.
        Assert.True(GoalResumeEligibility.IsResumable(
            GoalWith(GoalStatus.Failed, "Worker failed: build error in Coding"), GoalPhase.Failed, CanonicalBranch));
    }

    // ── The canonical branch rendering ───────────────────────────────────────

    [Theory]
    [InlineData("g1", "copilothive/g1")]
    [InlineData("goal-detail-resume-button-eligibility", "copilothive/goal-detail-resume-button-eligibility")]
    public void CanonicalCoderBranch_RendersTheGoalId(string goalId, string expected) =>
        Assert.Equal(expected, GoalResumeEligibility.CanonicalCoderBranch(goalId));

    // ── The moved predicates (behaviour unchanged) ───────────────────────────

    [Theory]
    [InlineData("Cancelled by user")]
    [InlineData("cancelled by user")]
    [InlineData("CANCELLED BY USER")]
    public void IsCancellationFailure_ExactWordingAnyCase_IsTrue(string reason) =>
        Assert.True(GoalResumeEligibility.IsCancellationFailure(GoalWith(GoalStatus.Failed, reason)));

    [Theory]
    [InlineData("Cancelled by user (test)")]
    [InlineData("Cancelled by")]
    [InlineData("cancelled by user\n")]
    [InlineData("Worker failed: cancelled by user")]
    [InlineData(null)]
    [InlineData("")]
    public void IsCancellationFailure_AnythingElse_IsFalse(string? reason) =>
        Assert.False(GoalResumeEligibility.IsCancellationFailure(GoalWith(GoalStatus.Failed, reason)));

    [Theory]
    [InlineData(GoalStatus.Failed, "Exceeded max iterations", true)]
    [InlineData(GoalStatus.Failed, "Exceeded max iterations during merge conflict resolution", true)]
    [InlineData(GoalStatus.Failed, "exceeded MAX iterations", true)]
    [InlineData(GoalStatus.Failed, "Worker failed: no iterations left", false)]
    [InlineData(GoalStatus.Failed, "Review rejected the changes", false)]
    [InlineData(GoalStatus.Failed, null, false)]
    [InlineData(GoalStatus.Failed, "", false)]
    [InlineData(GoalStatus.Completed, "Exceeded max iterations", false)]
    [InlineData(GoalStatus.InProgress, "Exceeded max iterations", false)]
    public void IsIterationExhaustionFailure_RequiresFailedAndMaxIterations(
        GoalStatus status, string? reason, bool expected) =>
        Assert.Equal(expected, GoalResumeEligibility.IsIterationExhaustionFailure(GoalWith(status, reason)));

    [Theory]
    [InlineData(GoalStatus.Failed, "Worker failed: build error", true)]
    [InlineData(GoalStatus.Failed, "Cancelled by user (test)", true)]
    [InlineData(GoalStatus.Failed, "Cancelled by user", false)]
    [InlineData(GoalStatus.Completed, "Worker failed: build error", false)]
    [InlineData(GoalStatus.Pending, "Worker failed: build error", false)]
    public void IsResumeCandidateGoal_RequiresFailedAndNotCancelled(GoalStatus status, string? reason, bool expected) =>
        Assert.Equal(expected, GoalResumeEligibility.IsResumeCandidateGoal(GoalWith(status, reason)));

    /// <summary>
    /// The rule is a PURE READ of the goal: evaluating it leaves the instance and the failure
    /// reason exactly as they were — no status sync, no reason scrubbing, no mutation.
    /// </summary>
    [Fact]
    public void IsResumable_DoesNotMutateTheGoal()
    {
        var goal = GoalWith(GoalStatus.Failed, "Worker failed: build error");
        var snapshot = (goal.Status, goal.FailureReason, goal.CompletedAt, goal.Iterations);

        _ = GoalResumeEligibility.IsResumable(goal, GoalPhase.Failed, CanonicalBranch);
        _ = GoalResumeEligibility.IsResumable(goal, GoalPhase.Coding, coderBranch: null);
        _ = GoalResumeEligibility.IsResumable(goal, pipelinePhase: null, coderBranch: null);

        Assert.Equal(snapshot, (goal.Status, goal.FailureReason, goal.CompletedAt, goal.Iterations));
    }
}
