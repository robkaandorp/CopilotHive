using CopilotHive.Goals;

namespace CopilotHive.Services;

/// <summary>
/// Validates and normalises iteration plans to enforce multi-round coding safety invariants.
/// Extracted from <see cref="GoalDispatcher"/> — all logic is identical.
/// </summary>
public static class IterationPlanValidator
{
    /// <summary>
    /// Validates and normalises an IterationPlan to enforce multi-round coding safety invariants:
    /// - Each Coding must be immediately followed by Testing (auto-insert if missing)
    /// - Exactly one Review is required after all Coding+Testing pairs (auto-insert if missing)
    /// - DocWriting and Improve rules unchanged (zero or one of each, same position rules)
    /// - Must end with Merging (auto-append if missing)
    ///
    /// For example, the Brain proposes ["coding", "coding", "review"] → output ["coding", "testing", "coding", "testing", "review", "merging"].
    /// </summary>
    /// <param name="plan">The plan to validate (phases list is modified in place).</param>
    /// <returns>The same plan object with validated phases.</returns>
    internal static IterationPlan ValidatePlan(IterationPlan plan)
    {
        var phases = plan.Phases;

        // Rule 1: Must contain Coding OR DocWriting (docs-only plans are valid)
        if (!phases.Contains(GoalPhase.Coding) && !phases.Contains(GoalPhase.DocWriting))
        {
            phases.Insert(0, GoalPhase.Coding);
        }

        if (phases.Contains(GoalPhase.Coding))
        {
            // Rule 2: Each Coding must be immediately followed by Testing.
            // Iterate backward so insertions don't shift indices we're about to process.
            for (var i = phases.Count - 1; i >= 0; i--)
            {
                if (phases[i] == GoalPhase.Coding && (i + 1 >= phases.Count || phases[i + 1] != GoalPhase.Testing))
                {
                    phases.Insert(i + 1, GoalPhase.Testing);
                }
            }

            // Rule 3: Exactly one Review, after all Coding+Testing pairs.
            // Remove any existing Review entries, then insert one after the last Testing.
            phases.RemoveAll(p => p == GoalPhase.Review);
            var lastTestingIndex = phases.LastIndexOf(GoalPhase.Testing);
            if (lastTestingIndex >= 0)
            {
                phases.Insert(lastTestingIndex + 1, GoalPhase.Review);
            }
            else
            {
                // No Testing found (shouldn't happen after Rule 2) — insert after last Coding
                var lastCodingIndex = phases.LastIndexOf(GoalPhase.Coding);
                phases.Insert(lastCodingIndex >= 0 ? lastCodingIndex + 1 : 0, GoalPhase.Review);
            }
        }
        else
        {
            // Docs-only plans: insert Testing only when neither Testing nor Review is present.
            if (!phases.Contains(GoalPhase.Testing) && !phases.Contains(GoalPhase.Review))
            {
                var docWritingIndex = phases.IndexOf(GoalPhase.DocWriting);
                var insertAt = docWritingIndex >= 0 ? docWritingIndex + 1 : phases.Count;
                phases.Insert(insertAt, GoalPhase.Testing);
            }
        }

        // Rule 4: Must end with Merging — remove any misplaced entries, then append
        phases.RemoveAll(p => p == GoalPhase.Merging);
        phases.Add(GoalPhase.Merging);

        return plan;
    }

    // ── Strict (pure) validation ────────────────────────────────────────────

    /// <summary>Phases that may appear in an iteration plan (R6).</summary>
    private static readonly GoalPhase[] AllowedPhases =
    [
        GoalPhase.Coding,
        GoalPhase.Testing,
        GoalPhase.DocWriting,
        GoalPhase.Review,
        GoalPhase.Improve,
        GoalPhase.Merging,
    ];

    /// <summary>
    /// Validates an <see cref="IterationPlan"/> against the block-based plan grammar (rules R1-R7)
    /// WITHOUT modifying it. Invalid plans are rejected with actionable reasons that are fed back
    /// to the Brain so it can submit a corrected plan.
    /// </summary>
    /// <remarks>
    /// This method is PURE: neither <paramref name="plan"/> nor any of its properties are mutated.
    /// Reasons are ordered by ascending rule number (R1…R7) with exactly one reason per violated
    /// rule — multiple violations of the same rule are aggregated into that rule's single reason.
    /// </remarks>
    /// <param name="plan">The plan to validate. Never modified.</param>
    /// <returns>A <see cref="PlanValidationResult"/> describing validity and any rejection reasons.</returns>
    internal static PlanValidationResult ValidatePlanStrict(IterationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Read-only snapshot of the submitted order — nothing below writes to plan.Phases.
        var phases = plan.Phases;
        var fullSequence = $"[{string.Join(", ", phases)}]";
        var reasons = new List<string>();

        AddOccupancyReason(phases, fullSequence, reasons);              // R1
        AddContentBlockReasons(phases, reasons);                        // R2
        AddReviewReasons(phases, fullSequence, reasons);                // R3
        AddImproveReasons(phases, fullSequence, reasons);               // R4
        AddMergingReasons(phases, fullSequence, reasons);               // R5
        AddAllowedPhaseReasons(phases, reasons);                        // R6
        AddOrderingDependencyReasons(phases, fullSequence, reasons);    // R7

        return new PlanValidationResult(reasons);
    }

    /// <summary>True when the phase is a content phase (participates in a content block).</summary>
    private static bool IsContentPhase(GoalPhase phase)
        => phase is GoalPhase.Coding or GoalPhase.DocWriting;

    /// <summary>R1 Occupancy: at least one Coding or DocWriting must be present.</summary>
    private static void AddOccupancyReason(List<GoalPhase> phases, string fullSequence, List<string> reasons)
    {
        if (phases.Count == 0)
        {
            reasons.Add(
                "R1 (Occupancy): the plan is empty — it must contain at least one Coding or DocWriting phase. "
                + $"Submitted plan: {fullSequence}.");
            return;
        }

        if (!phases.Any(IsContentPhase))
        {
            reasons.Add(
                "R1 (Occupancy): the plan contains neither Coding nor DocWriting — at least one is required. "
                + $"Submitted plan: {fullSequence}.");
        }
    }

    /// <summary>
    /// R2 Testing after each content block: every maximal run of Coding/DocWriting must be
    /// immediately followed by exactly one Testing, and every Testing must directly follow a content block.
    /// </summary>
    private static void AddContentBlockReasons(List<GoalPhase> phases, List<string> reasons)
    {
        var violations = new List<string>();

        // Single pass in index order so violations are reported in the order they appear in the plan.
        for (var i = 0; i < phases.Count; i++)
        {
            if (IsContentPhase(phases[i]))
            {
                var start = i;
                var end = i;
                while (end + 1 < phases.Count && IsContentPhase(phases[end + 1]))
                {
                    end++;
                }

                i = end;

                if (end + 1 >= phases.Count || phases[end + 1] != GoalPhase.Testing)
                {
                    var follower = end + 1 < phases.Count ? phases[end + 1].ToString() : "end of plan";
                    violations.Add(
                        $"content block {Quote(phases, start, end)} at index {start}..{end} is followed by "
                        + $"{follower} instead of exactly one Testing, in {Quote(phases, start, end + 1)}");
                }

                continue;
            }

            if (phases[i] == GoalPhase.Testing && (i == 0 || !IsContentPhase(phases[i - 1])))
            {
                var predecessor = i == 0 ? "start of plan" : phases[i - 1].ToString();
                violations.Add(
                    $"stray Testing at index {i}: it follows {predecessor} instead of a Coding/DocWriting "
                    + $"content block, in {Quote(phases, i - 1, i + 1)}");
            }
        }

        if (violations.Count > 0)
        {
            reasons.Add(
                "R2 (Testing after each content block): "
                + string.Join("; ", violations)
                + ". Every content block (a contiguous run of Coding/DocWriting) must be immediately "
                + "followed by exactly one Testing.");
        }
    }

    /// <summary>R3 Review: exactly one Review, after all content-block + Testing rounds.</summary>
    private static void AddReviewReasons(List<GoalPhase> phases, string fullSequence, List<string> reasons)
    {
        var violations = new List<string>();
        var reviewIndexes = IndexesOf(phases, GoalPhase.Review);

        if (reviewIndexes.Count == 0)
        {
            violations.Add($"no Review phase was submitted — exactly one is required. Submitted plan: {fullSequence}");
        }
        else
        {
            if (reviewIndexes.Count > 1)
            {
                violations.Add(
                    $"{reviewIndexes.Count} Review phases found at index {string.Join(", ", reviewIndexes)} — "
                    + $"exactly one is allowed, in {Quote(phases, reviewIndexes[0], reviewIndexes[^1])}");
            }

            var firstReview = reviewIndexes[0];
            for (var i = firstReview + 1; i < phases.Count; i++)
            {
                if (IsContentPhase(phases[i]) || phases[i] == GoalPhase.Testing)
                {
                    violations.Add(
                        $"{phases[i]} at index {i} appears after the Review at index {firstReview}, "
                        + $"in {Quote(phases, firstReview, i)}");
                }
            }
        }

        if (violations.Count > 0)
        {
            reasons.Add(
                "R3 (Review): "
                + string.Join("; ", violations)
                + ". Exactly one Review must follow all content-block + Testing rounds, with no Coding, "
                + "DocWriting or Testing after it.");
        }
    }

    /// <summary>
    /// R4 Improve: at most one Improve, positioned after the Review and before Merging.
    /// The position checks run even when Review or Merging is absent — an Improve without a
    /// preceding Review (or without a following Merging) is misplaced by definition.
    /// </summary>
    private static void AddImproveReasons(List<GoalPhase> phases, string fullSequence, List<string> reasons)
    {
        var improveIndexes = IndexesOf(phases, GoalPhase.Improve);
        if (improveIndexes.Count == 0)
        {
            return;
        }

        var violations = new List<string>();

        // (c) Duplicates are invalid regardless of Review/Merging presence.
        if (improveIndexes.Count > 1)
        {
            violations.Add(
                $"{improveIndexes.Count} Improve phases found at index {string.Join(", ", improveIndexes)} — "
                + $"at most one is allowed, in {Quote(phases, improveIndexes[0], improveIndexes[^1])}");
        }

        var reviewIndex = phases.IndexOf(GoalPhase.Review);
        var mergingIndex = phases.IndexOf(GoalPhase.Merging);

        foreach (var index in improveIndexes)
        {
            // (a) Improve must come after a Review. No Review at all → every Improve is misplaced.
            if (reviewIndex < 0)
            {
                violations.Add(
                    $"Improve at index {index} has no preceding Review — Improve is only allowed after the "
                    + $"Review. Submitted plan: {fullSequence}");
            }
            else if (index < reviewIndex)
            {
                violations.Add(
                    $"Improve at index {index} appears before the Review at index {reviewIndex}, "
                    + $"in {Quote(phases, index, reviewIndex)}");
            }

            // (b) Improve must come before a Merging. No Merging at all → every Improve is misplaced.
            if (mergingIndex < 0)
            {
                violations.Add(
                    $"Improve at index {index} has no following Merging — Improve is only allowed before the "
                    + $"Merging. Submitted plan: {fullSequence}");
            }
            else if (index > mergingIndex)
            {
                violations.Add(
                    $"Improve at index {index} appears after the Merging at index {mergingIndex}, "
                    + $"in {Quote(phases, mergingIndex, index)}");
            }
        }

        if (violations.Count > 0)
        {
            reasons.Add(
                "R4 (Improve): "
                + string.Join("; ", violations)
                + ". At most one Improve is allowed, positioned after the Review and before Merging.");
        }
    }

    /// <summary>R5 Merging: exactly one Merging and it must be the final phase.</summary>
    private static void AddMergingReasons(List<GoalPhase> phases, string fullSequence, List<string> reasons)
    {
        var violations = new List<string>();
        var mergingIndexes = IndexesOf(phases, GoalPhase.Merging);

        if (mergingIndexes.Count == 0)
        {
            violations.Add(
                $"no Merging phase was submitted — exactly one is required as the final phase. Submitted plan: {fullSequence}");
        }
        else
        {
            if (mergingIndexes.Count > 1)
            {
                violations.Add(
                    $"{mergingIndexes.Count} Merging phases found at index {string.Join(", ", mergingIndexes)} — "
                    + $"exactly one is allowed, in {Quote(phases, mergingIndexes[0], mergingIndexes[^1])}");
            }

            foreach (var index in mergingIndexes)
            {
                if (index != phases.Count - 1)
                {
                    violations.Add(
                        $"Merging at index {index} is not the final phase (it is followed by {phases[index + 1]}), "
                        + $"in {Quote(phases, index, phases.Count - 1)}");
                }
            }
        }

        if (violations.Count > 0)
        {
            reasons.Add(
                "R5 (Merging): "
                + string.Join("; ", violations)
                + ". Exactly one Merging is required and it must be the last phase of the plan.");
        }
    }

    /// <summary>R6 Allowed phases only: enum membership check for the six supported phases.</summary>
    private static void AddAllowedPhaseReasons(List<GoalPhase> phases, List<string> reasons)
    {
        var violations = new List<string>();

        for (var i = 0; i < phases.Count; i++)
        {
            if (!AllowedPhases.Contains(phases[i]))
            {
                violations.Add($"{phases[i]} at index {i}, in {Quote(phases, i - 1, i + 1)}");
            }
        }

        if (violations.Count > 0)
        {
            reasons.Add(
                "R6 (Allowed phases only): unsupported phase(s) "
                + string.Join("; ", violations)
                + $". Only {string.Join(", ", AllowedPhases)} may appear in an iteration plan.");
        }
    }

    /// <summary>
    /// R7 Ordering-dependency: when both content types are present and the first Testing occurs
    /// before the first occurrence of exactly one of them, suggest consolidating them into a
    /// single content block before that Testing.
    /// </summary>
    private static void AddOrderingDependencyReasons(List<GoalPhase> phases, string fullSequence, List<string> reasons)
    {
        var firstTesting = phases.IndexOf(GoalPhase.Testing);
        var firstCoding = phases.IndexOf(GoalPhase.Coding);
        var firstDocWriting = phases.IndexOf(GoalPhase.DocWriting);

        if (firstTesting < 0 || firstCoding < 0 || firstDocWriting < 0)
        {
            return;
        }

        var codingAfter = firstCoding > firstTesting;
        var docWritingAfter = firstDocWriting > firstTesting;

        // Only the FIRST type/Testing interleaving matters; later blocks are governed by R2-R5.
        if (codingAfter == docWritingAfter)
        {
            return;
        }

        var lateType = docWritingAfter ? GoalPhase.DocWriting : GoalPhase.Coding;
        var lateIndex = docWritingAfter ? firstDocWriting : firstCoding;
        var suggestion = docWritingAfter ? "Coding → DocWriting → Testing" : "DocWriting → Coding → Testing";

        reasons.Add(
            $"R7 (Ordering-dependency): the first Testing at index {firstTesting} occurs before the first "
            + $"{lateType} at index {lateIndex}, so the {lateType} work is not covered by that Testing round. "
            + $"Consolidate both content phases into a single block before the first Testing: {suggestion}. "
            + $"Submitted plan: {fullSequence}.");
    }

    /// <summary>
    /// Formats the sub-sequence of <paramref name="phases"/> between <paramref name="from"/> and
    /// <paramref name="to"/> (inclusive) as a bracketed list, clamped to the plan bounds.
    /// Used so every rejection reason quotes the offending sequence.
    /// </summary>
    private static string Quote(List<GoalPhase> phases, int from, int to)
    {
        var start = Math.Clamp(from, 0, Math.Max(phases.Count - 1, 0));
        var end = Math.Clamp(to, 0, Math.Max(phases.Count - 1, 0));
        if (phases.Count == 0 || end < start)
        {
            return "[]";
        }

        return $"[{string.Join(", ", phases.GetRange(start, end - start + 1))}]";
    }

    /// <summary>Returns every index at which <paramref name="phase"/> occurs, in plan order.</summary>
    private static List<int> IndexesOf(List<GoalPhase> phases, GoalPhase phase)
    {
        var indexes = new List<int>();
        for (var i = 0; i < phases.Count; i++)
        {
            if (phases[i] == phase)
            {
                indexes.Add(i);
            }
        }

        return indexes;
    }

    /// <summary>
    /// Builds a system note describing how the Brain's iteration plan was modified by
    /// <see cref="ValidatePlan"/> to satisfy safety requirements.
    /// Generates accurate per-change reasons for each adjustment made.
    /// </summary>
    /// <param name="original">The phases from the Brain's original plan.</param>
    /// <param name="final">The phases after validation was applied.</param>
    /// <returns>A human-readable note describing what was adjusted and why.</returns>
    internal static string BuildPlanAdjustmentNote(List<GoalPhase> original, List<GoalPhase> final)
    {
        var originalSet = new HashSet<GoalPhase>(original);
        var adjustments = new List<string>();

        // Coding was added as safety fallback (neither Coding nor DocWriting was present)
        if (!originalSet.Contains(GoalPhase.Coding) && !originalSet.Contains(GoalPhase.DocWriting)
            && final.Contains(GoalPhase.Coding))
        {
            adjustments.Add("- Coding was inserted at the start (required: every plan must contain Coding or DocWriting)");
        }

        // Testing was added — reference the actual preceding phase
        if (!originalSet.Contains(GoalPhase.Testing) && final.Contains(GoalPhase.Testing))
        {
            if (final.Contains(GoalPhase.Coding))
            {
                adjustments.Add("- Testing was inserted after Coding (required for code-change plans)");
            }
            else
            {
                // Docs-only plan: Testing inserted after DocWriting
                adjustments.Add("- Testing was inserted after DocWriting (required: docs-only plan had neither Testing nor Review)");
            }
        }

        // Review was added to a code-change plan
        if (!originalSet.Contains(GoalPhase.Review) && final.Contains(GoalPhase.Review))
        {
            adjustments.Add("- Review was inserted after Testing (required for code-change plans)");
        }

        // Merging adjustments: appended (absent) or moved to the end (misplaced)
        if (!originalSet.Contains(GoalPhase.Merging) && final.Contains(GoalPhase.Merging))
        {
            adjustments.Add("- Merging was appended as the final phase (always required)");
        }
        else
        {
            var originalMergingIndex = original.IndexOf(GoalPhase.Merging);
            var finalMergingIndex = final.IndexOf(GoalPhase.Merging);
            var mergingWasMoved = originalSet.Contains(GoalPhase.Merging)
                && originalMergingIndex != original.Count - 1
                && finalMergingIndex == final.Count - 1;
            if (mergingWasMoved)
            {
                adjustments.Add("- Merging was moved to the end (always required as the last phase)");
            }
        }

        var adjustmentsText = adjustments.Count > 0
            ? string.Join("\n", adjustments)
            : "- (phases were reordered to satisfy safety invariants)";

        return $"""
Your iteration plan was adjusted by the system to meet safety requirements.
Original plan: [{string.Join(", ", original)}]
Final plan: [{string.Join(", ", final)}]
Adjustments:
{adjustmentsText}
You will be asked to craft prompts for ALL phases in the final plan, including any that were added.
""";
    }
}

/// <summary>
/// Outcome of a strict (non-mutating) iteration plan validation performed by
/// <see cref="IterationPlanValidator.ValidatePlanStrict"/>.
/// </summary>
internal sealed class PlanValidationResult
{
    /// <summary>Creates a result from the rejection reasons produced by validation.</summary>
    /// <param name="rejectionReasons">The rejection reasons, ordered by ascending rule number. Empty when the plan is valid.</param>
    internal PlanValidationResult(IEnumerable<string> rejectionReasons)
    {
        RejectionReasons = rejectionReasons is null ? [] : rejectionReasons.ToArray();
    }

    /// <summary>True when the plan satisfied every rule (no rejection reasons).</summary>
    internal bool IsValid => RejectionReasons.Count == 0;

    /// <summary>
    /// The rejection reasons, ordered by ascending rule number (R1…R7) with at most one reason per
    /// violated rule. Empty when <see cref="IsValid"/> is true.
    /// </summary>
    internal IReadOnlyList<string> RejectionReasons { get; }
}
