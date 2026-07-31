using CopilotHive.Goals;
using CopilotHive.Services;
using CopilotHive.Workers;

namespace CopilotHive.Tests;

/// <summary>
/// Tests for the pure, block-based plan grammar implemented by
/// <see cref="IterationPlanValidator.ValidatePlanStrict"/> (rules R1-R7).
/// </summary>
public sealed class ValidatePlanStrictTests
{
    private static IterationPlan Plan(params GoalPhase[] phases)
        => new() { Phases = [.. phases] };

    private static string AllReasons(PlanValidationResult result)
        => string.Join(" | ", result.RejectionReasons);

    private static string RulePrefix(string reason) => reason[..2];

    // ── 1. Purity tests (REQUIRED) ──────────────────────────────────────────

    [Fact]
    public void ValidatePlanStrict_ValidPlan_DoesNotMutatePlan()
    {
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging],
            PhaseInstructions = { ["coding"] = "do the thing" },
            PhaseTiers = { [GoalPhase.Coding] = ModelTier.Premium },
            Reason = "because",
        };
        var phasesSnapshot = plan.Phases.ToList();
        var instructionsSnapshot = plan.PhaseInstructions.ToDictionary(kv => kv.Key, kv => kv.Value);
        var tiersSnapshot = plan.PhaseTiers.ToDictionary(kv => kv.Key, kv => kv.Value);
        var reasonSnapshot = plan.Reason;

        var result = IterationPlanValidator.ValidatePlanStrict(plan);

        Assert.True(result.IsValid);
        Assert.Equal(phasesSnapshot, plan.Phases);
        Assert.Equal(instructionsSnapshot, plan.PhaseInstructions);
        Assert.Equal(tiersSnapshot, plan.PhaseTiers);
        Assert.Equal(reasonSnapshot, plan.Reason);
    }

    [Fact]
    public void ValidatePlanStrict_InvalidPlan_DoesNotMutatePlan()
    {
        // R7 violation: Coding → Testing → DocWriting → Testing
        var plan = new IterationPlan
        {
            Phases = [GoalPhase.Coding, GoalPhase.Testing, GoalPhase.DocWriting, GoalPhase.Testing],
            PhaseInstructions = { ["coding"] = "code", ["docwriting"] = "docs" },
            PhaseTiers = { [GoalPhase.Coding] = ModelTier.Premium, [GoalPhase.Testing] = ModelTier.Standard },
            Reason = "broken plan",
        };
        var phasesSnapshot = plan.Phases.ToList();
        var instructionsSnapshot = plan.PhaseInstructions.ToDictionary(kv => kv.Key, kv => kv.Value);
        var tiersSnapshot = plan.PhaseTiers.ToDictionary(kv => kv.Key, kv => kv.Value);
        var reasonSnapshot = plan.Reason;

        var result = IterationPlanValidator.ValidatePlanStrict(plan);

        Assert.False(result.IsValid);
        Assert.Equal(phasesSnapshot, plan.Phases);
        Assert.Equal(instructionsSnapshot, plan.PhaseInstructions);
        Assert.Equal(tiersSnapshot, plan.PhaseTiers);
        Assert.Equal(reasonSnapshot, plan.Reason);
    }

    // ── 2. Accept tests (IsValid == true, RejectionReasons empty) ──────────

    [Fact]
    public void ValidatePlanStrict_CodingTestingReviewMerging_IsValid()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging));

        Assert.True(result.IsValid, AllReasons(result));
        Assert.Empty(result.RejectionReasons);
    }

    [Fact]
    public void ValidatePlanStrict_CodingDocWritingTestingReviewMerging_IsValid()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.DocWriting, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging));

        Assert.True(result.IsValid, AllReasons(result));
        Assert.Empty(result.RejectionReasons);
    }

    [Fact]
    public void ValidatePlanStrict_DocWritingCodingTestingReviewMerging_IsValid()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.DocWriting, GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging));

        Assert.True(result.IsValid, AllReasons(result));
        Assert.Empty(result.RejectionReasons);
    }

    [Fact]
    public void ValidatePlanStrict_DocWritingTestingReviewMerging_IsValid()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.DocWriting, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging));

        Assert.True(result.IsValid, AllReasons(result));
        Assert.Empty(result.RejectionReasons);
    }

    [Fact]
    public void ValidatePlanStrict_MultiRoundCodingTesting_IsValid()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Testing,
                 GoalPhase.Review, GoalPhase.Merging));

        Assert.True(result.IsValid, AllReasons(result));
        Assert.Empty(result.RejectionReasons);
    }

    [Fact]
    public void ValidatePlanStrict_WithImproveBeforeMerging_IsValid()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Improve, GoalPhase.Merging));

        Assert.True(result.IsValid, AllReasons(result));
        Assert.Empty(result.RejectionReasons);
    }

    [Fact]
    public void ValidatePlanStrict_R7NoFire_BothContentTypesPrecedeFirstTesting_IsValid()
    {
        // Both content types precede the first Testing — R7 must NOT fire.
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.DocWriting, GoalPhase.Testing, GoalPhase.Coding,
                 GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging));

        Assert.True(result.IsValid, AllReasons(result));
        Assert.Empty(result.RejectionReasons);
    }

    // ── 3. Reject tests — grammar ──────────────────────────────────────────

    [Fact]
    public void ValidatePlanStrict_EmptyPlan_RejectsWithR1QuotingEmptySequence()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(Plan());

        Assert.False(result.IsValid);
        var r1 = Assert.Single(result.RejectionReasons, r => r.StartsWith("R1", StringComparison.Ordinal));
        Assert.Contains("[]", r1, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePlanStrict_NeitherCodingNorDocWriting_RejectsWithR1QuotingFullSequence()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Review, GoalPhase.Merging));

        Assert.False(result.IsValid);
        var r1 = Assert.Single(result.RejectionReasons, r => r.StartsWith("R1", StringComparison.Ordinal));
        Assert.Contains("[Review, Merging]", r1, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePlanStrict_ContentBlockWithNoFollowingTesting_RejectsWithR2()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Review, GoalPhase.Merging));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R2", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_StrayTestingLeading_RejectsWithR2()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R2", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_StrayTestingConsecutiveDuplicate_RejectsWithR2()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R2", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_StrayTestingAfterReview_RejectsWithR2AndR3()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Testing, GoalPhase.Merging));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R2", StringComparison.Ordinal));
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R3", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_ContentAfterReview_RejectsWithR3()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Coding, GoalPhase.Merging));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R3", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_TwoReviews_RejectsWithSingleAggregatedR3()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Review, GoalPhase.Merging));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R3", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_ImproveBeforeReview_RejectsWithR4()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Improve, GoalPhase.Review, GoalPhase.Merging));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R4", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_DuplicateImprove_RejectsWithExactlyOneR4()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Improve,
                 GoalPhase.Improve, GoalPhase.Merging));

        Assert.False(result.IsValid);
        var r4s = result.RejectionReasons.Where(r => r.StartsWith("R4", StringComparison.Ordinal)).ToList();
        Assert.Single(r4s);
    }

    [Fact]
    public void ValidatePlanStrict_ImproveAfterMerging_RejectsWithR4()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging, GoalPhase.Improve));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R4", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_MissingMerging_RejectsWithR5QuotingFullSequence()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review));

        Assert.False(result.IsValid);
        var r5 = Assert.Single(result.RejectionReasons, r => r.StartsWith("R5", StringComparison.Ordinal));
        Assert.Contains("[Coding, Testing, Review]", r5, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePlanStrict_MisplacedMerging_RejectsWithR5()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Merging, GoalPhase.Testing, GoalPhase.Review));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R5", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_DuplicateMerging_RejectsWithR5()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging, GoalPhase.Merging));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R5", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_UnsupportedPhasePlanning_RejectsWithR6()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Planning, GoalPhase.Review, GoalPhase.Merging));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R6", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_UnsupportedPhaseDone_RejectsWithR6()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging, GoalPhase.Done));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R6", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_UnsupportedPhaseFailed_RejectsWithR6()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Failed, GoalPhase.Merging));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R6", StringComparison.Ordinal));
    }

    // ── 4. R7 reject tests ─────────────────────────────────────────────────

    [Fact]
    public void ValidatePlanStrict_R7_CodingTestingDocWritingTesting_RejectsWithR7SuggestingCodingDocWritingTesting()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.DocWriting, GoalPhase.Testing));

        Assert.False(result.IsValid);
        var r7 = Assert.Single(result.RejectionReasons, r => r.StartsWith("R7", StringComparison.Ordinal));
        Assert.Contains("Coding → DocWriting → Testing", r7, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePlanStrict_R7_DocWritingTestingCodingTesting_RejectsWithR7SuggestingDocWritingCodingTesting()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.DocWriting, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Testing));

        Assert.False(result.IsValid);
        var r7 = Assert.Single(result.RejectionReasons, r => r.StartsWith("R7", StringComparison.Ordinal));
        Assert.Contains("DocWriting → Coding → Testing", r7, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePlanStrict_R7_ConsolidatedCodingDocWritingTesting_NoR7FalsePositive()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.DocWriting, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging));

        Assert.True(result.IsValid, AllReasons(result));
        Assert.DoesNotContain(result.RejectionReasons, r => r.StartsWith("R7", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_R7_ConsolidatedDocWritingCodingTesting_NoR7FalsePositive()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.DocWriting, GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging));

        Assert.True(result.IsValid, AllReasons(result));
        Assert.DoesNotContain(result.RejectionReasons, r => r.StartsWith("R7", StringComparison.Ordinal));
    }

    // ── 5. Reason ordering tests ───────────────────────────────────────────

    [Fact]
    public void ValidatePlanStrict_MultipleRuleViolations_ReasonsOrderedByAscendingRuleNumber()
    {
        // [Testing, Planning, Improve, Review] — R1 no content, R2 stray leading Testing,
        // R4 Improve before Review (and no Merging), R5 no Merging, R6 Planning.
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Testing, GoalPhase.Planning, GoalPhase.Improve, GoalPhase.Review));

        Assert.False(result.IsValid);
        var ruleNumbers = result.RejectionReasons.Select(RulePrefix).ToList();
        // Reasons must be ordered by ascending rule number (R1 before R2 before R4 before R5 before R6).
        Assert.Equal(ruleNumbers.OrderBy(r => r, StringComparer.Ordinal), ruleNumbers);
        Assert.Contains("R1", ruleNumbers);
        Assert.Contains("R2", ruleNumbers);
        Assert.Contains("R4", ruleNumbers);
        Assert.Contains("R5", ruleNumbers);
        Assert.Contains("R6", ruleNumbers);
    }

    [Fact]
    public void ValidatePlanStrict_TwoReviewsAndContentAfterReview_ExactlyOneR3Reason()
    {
        // Two Reviews AND content after Review — should aggregate into exactly ONE R3 reason.
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Review,
                 GoalPhase.Coding, GoalPhase.Merging));

        Assert.False(result.IsValid);
        var r3s = result.RejectionReasons.Where(r => r.StartsWith("R3", StringComparison.Ordinal)).ToList();
        Assert.Single(r3s);
    }

    [Fact]
    public void ValidatePlanStrict_AbsenceBasedReason_EmptyPlan_ContainsEmptySequence()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(Plan());

        Assert.False(result.IsValid);
        var all = AllReasons(result);
        Assert.Contains("[]", all, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePlanStrict_AbsenceBasedReason_MissingMerging_ContainsFullSubmittedSequence()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review));

        Assert.False(result.IsValid);
        var r5 = Assert.Single(result.RejectionReasons, r => r.StartsWith("R5", StringComparison.Ordinal));
        Assert.Contains("[Coding, Testing, Review]", r5, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePlanStrict_AbsenceBasedReason_MissingReview_ContainsFullSubmittedSequence()
    {
        // Missing Review is an absence-based R3 violation — it must quote the full submitted sequence.
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Merging));

        Assert.False(result.IsValid);
        var r3 = Assert.Single(result.RejectionReasons, r => r.StartsWith("R3", StringComparison.Ordinal));
        Assert.Contains("[Coding, Testing, Merging]", r3, StringComparison.Ordinal);
    }

    // ── 6. R4 must not be silently omitted when Review/Merging are absent ──

    [Fact]
    public void ValidatePlanStrict_ImproveWithNoReviewInPlan_RejectsWithR4()
    {
        // Improve requires a preceding Review — with no Review at all, the Improve is misplaced.
        // Merging exists and is final, so R5 must NOT fire.
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Improve, GoalPhase.Merging));

        Assert.False(result.IsValid);
        var r4 = Assert.Single(result.RejectionReasons, r => r.StartsWith("R4", StringComparison.Ordinal));
        Assert.Contains("Improve at index 2", r4, StringComparison.Ordinal);
        // R3 also fires for the missing Review, but R4 must NOT be silently omitted.
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R3", StringComparison.Ordinal));
        // R5 must NOT fire — Merging is present and is the final phase.
        Assert.DoesNotContain(result.RejectionReasons, r => r.StartsWith("R5", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_ImproveWithNoReviewAndNoMerging_RejectsWithR4R3R5()
    {
        // Improve present, no Review, no Merging — R4 must fire (removal-proof: if the R4 fix
        // is reverted to be conditional on Review/Merging existence, R4 would be silently omitted
        // and this test would fail).
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Improve));

        Assert.False(result.IsValid);
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R4", StringComparison.Ordinal));
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R3", StringComparison.Ordinal));
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R5", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_ImproveWithNoMergingInPlan_RejectsWithR4()
    {
        // Improve must precede a Merging — with no Merging at all, the Improve is misplaced.
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Improve));

        Assert.False(result.IsValid);
        var r4 = Assert.Single(result.RejectionReasons, r => r.StartsWith("R4", StringComparison.Ordinal));
        Assert.Contains("Improve at index 3", r4, StringComparison.Ordinal);
        // R5 also fires for the missing Merging, but R4 must NOT be silently omitted.
        Assert.Single(result.RejectionReasons, r => r.StartsWith("R5", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_DuplicateImproveWithoutReviewOrMerging_RejectsWithSingleAggregatedR4()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Improve, GoalPhase.Improve));

        Assert.False(result.IsValid);
        var r4 = Assert.Single(result.RejectionReasons, r => r.StartsWith("R4", StringComparison.Ordinal));
        Assert.Contains("2 Improve phases", r4, StringComparison.Ordinal);
    }

    // ── 7. R2 violations are listed in plan order ─────────────────────────

    [Fact]
    public void ValidatePlanStrict_R2Violations_ListedInAscendingPlanOrder()
    {
        // Stray Testing at index 0 must be listed BEFORE the un-tested content block at index 1.
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Review, GoalPhase.Merging));

        var r2 = Assert.Single(result.RejectionReasons, r => r.StartsWith("R2", StringComparison.Ordinal));
        var strayPosition = r2.IndexOf("stray Testing at index 0", StringComparison.Ordinal);
        var blockPosition = r2.IndexOf("content block [Coding] at index 1", StringComparison.Ordinal);
        Assert.True(strayPosition >= 0, r2);
        Assert.True(blockPosition >= 0, r2);
        Assert.True(strayPosition < blockPosition, $"R2 violations out of plan order: {r2}");
    }

    [Fact]
    public void ValidatePlanStrict_R2Violations_LeadingStrayBeforeConsecutiveDuplicate()
    {
        // [Testing, Coding, Testing, Testing, Review, Merging]
        // Leading stray Testing at index 0, content block (Coding at index 1) with following
        // Testing at index 2 (valid), then consecutive duplicate Testing at index 3.
        // The leading stray (index 0) must be listed BEFORE the consecutive duplicate (index 3).
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Testing,
                 GoalPhase.Review, GoalPhase.Merging));

        var r2 = Assert.Single(result.RejectionReasons, r => r.StartsWith("R2", StringComparison.Ordinal));
        var firstStray = r2.IndexOf("stray Testing at index 0", StringComparison.Ordinal);
        var secondStray = r2.IndexOf("stray Testing at index 3", StringComparison.Ordinal);
        Assert.True(firstStray >= 0, $"leading stray not found: {r2}");
        Assert.True(secondStray >= 0, $"consecutive duplicate stray not found: {r2}");
        Assert.True(firstStray < secondStray, $"R2 violations out of plan order: {r2}");
    }

    // ── 8. Every rejection reason quotes a bracketed sequence ─────────────

    [Theory]
    // R2 stray Testing + un-tested block
    [InlineData(new[] { GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Review, GoalPhase.Merging })]
    // R3 duplicate Review + content after Review
    [InlineData(new[] { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Review, GoalPhase.Coding, GoalPhase.Merging })]
    // R4 Improve before Review
    [InlineData(new[] { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Improve, GoalPhase.Review, GoalPhase.Merging })]
    // R4 Improve after Merging + R5 misplaced Merging
    [InlineData(new[] { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging, GoalPhase.Improve })]
    // R5 duplicate Merging
    [InlineData(new[] { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging, GoalPhase.Merging })]
    // R6 unsupported phase
    [InlineData(new[] { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Planning, GoalPhase.Review, GoalPhase.Merging })]
    // R7 ordering dependency
    [InlineData(new[] { GoalPhase.Coding, GoalPhase.Testing, GoalPhase.DocWriting, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Merging })]
    // R1 absence
    [InlineData(new[] { GoalPhase.Review, GoalPhase.Merging })]
    public void ValidatePlanStrict_EveryRejectionReason_QuotesABracketedSequence(GoalPhase[] phases)
    {
        var result = IterationPlanValidator.ValidatePlanStrict(Plan(phases));

        Assert.False(result.IsValid);
        Assert.All(result.RejectionReasons, reason =>
        {
            var open = reason.IndexOf('[', StringComparison.Ordinal);
            Assert.True(open >= 0, $"reason has no quoted sequence: {reason}");
            Assert.True(reason.IndexOf(']', open) > open, $"reason has no quoted sequence: {reason}");
        });
    }

    // ── 9. Additional coverage ────────────────────────────────────────────

    [Fact]
    public void ValidatePlanStrict_DuplicateReview_ReportsR3AndNotR6()
    {
        // R6 is EXCLUSIVELY enum membership — a duplicate Review is an R3 concern only.
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review, GoalPhase.Review, GoalPhase.Merging));

        Assert.Single(result.RejectionReasons, r => r.StartsWith("R3", StringComparison.Ordinal));
        Assert.DoesNotContain(result.RejectionReasons, r => r.StartsWith("R6", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_MultipleUnsupportedPhases_ProduceSingleAggregatedR6Reason()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Planning, GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Review,
                 GoalPhase.Done, GoalPhase.Merging));

        var r6 = Assert.Single(result.RejectionReasons, r => r.StartsWith("R6", StringComparison.Ordinal));
        Assert.Contains("Planning", r6, StringComparison.Ordinal);
        Assert.Contains("Done", r6, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatePlanStrict_EmptyPlan_ReportsR1R3R5InAscendingOrder()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(Plan());

        Assert.Equal(["R1", "R3", "R5"], result.RejectionReasons.Select(RulePrefix));
        Assert.All(result.RejectionReasons, r => Assert.Contains("[]", r, StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatePlanStrict_SingleContentType_DoesNotTriggerR7()
    {
        var result = IterationPlanValidator.ValidatePlanStrict(
            Plan(GoalPhase.Coding, GoalPhase.Testing, GoalPhase.Coding, GoalPhase.Testing,
                 GoalPhase.Review, GoalPhase.Merging));

        Assert.DoesNotContain(result.RejectionReasons, r => r.StartsWith("R7", StringComparison.Ordinal));
    }
}