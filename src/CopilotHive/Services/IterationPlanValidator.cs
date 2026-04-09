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
