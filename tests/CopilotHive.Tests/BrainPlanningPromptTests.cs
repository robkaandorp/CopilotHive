using CopilotHive.Goals;
using CopilotHive.Orchestration;
using CopilotHive.Services;

using Xunit;

namespace CopilotHive.Tests;

/// <summary>
/// Text-level tests for <see cref="BrainPromptBuilder.BuildPlanningPrompt"/> ensuring the planning
/// prompt documents the block-based plan grammar (R1-R7) and the phase-name rejection rules.
/// </summary>
public sealed class BrainPlanningPromptTests
{
    private static string BuildPrompt()
    {
        var pipeline = new GoalPipeline(new Goal
        {
            Id = "test-goal",
            Description = "Test goal",
            RepositoryNames = ["repo"],
        });

        return BrainPromptBuilder.BuildPlanningPrompt(pipeline);
    }

    private static string BuildRetryPrompt()
    {
        var pipeline = new GoalPipeline(new Goal
        {
            Id = "test-goal",
            Description = "Test goal",
            RepositoryNames = ["repo"],
        });

        // Iteration is computed from IterationBudget.Used + 1. Consume one use so Iteration == 2.
        pipeline.IterationBudget.TryConsume();

        return BrainPromptBuilder.BuildPlanningPrompt(pipeline);
    }

    /// <summary>
    /// Builds the planning prompt with every run of whitespace collapsed to a single space, so
    /// assertions can match a full guidance sentence that the raw string literal wraps across
    /// several indented source lines.
    /// </summary>
    private static string BuildPromptCollapsed() =>
        System.Text.RegularExpressions.Regex.Replace(BuildPrompt(), @"\s+", " ");

    [Fact]
    public void BuildPlanningPrompt_ContainsR1OccupancyRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R1 (Occupancy)", prompt);
        Assert.Contains("at least one Coding or DocWriting", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsR2ContentBlockRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R2 (Testing after each content block)", prompt);
        Assert.Contains("content block", prompt);
        Assert.Contains("maximal contiguous run", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsR3ReviewRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R3 (Review)", prompt);
        Assert.Contains("exactly one Review", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsR4ImproveRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R4 (Improve)", prompt);
        Assert.Contains("at most one Improve", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_FirstIterationDoesNotRecommendImprove()
    {
        var prompt = BuildPrompt();

        // The old unconditional recommendation is gone.
        Assert.DoesNotContain("Include the improve phase to let the improver refine", prompt);

        // The conditional guidance is present for clean first iterations.
        Assert.Contains("Include the improve phase ONLY when this iteration had previous issues", prompt);
        Assert.Contains("For a clean first iteration with no prior failures, do NOT", prompt);
        Assert.Contains("a clean plan should omit it", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_RetryIterationRecommendsImprove()
    {
        var prompt = BuildRetryPrompt();

        // The shared prompt body still contains the conditional Improve guidance,
        // which now applies because this is a retry iteration.
        Assert.Contains("Include the improve phase ONLY when this iteration had previous issues", prompt);
        Assert.Contains("second-or-later iteration with prior feedback", prompt);

        // The retry-context block fires for Iteration > 1.
        Assert.Contains("This is a retry — use the feedback above", prompt);
        Assert.Contains("iteration 2", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_R4ImproveRulePreserved()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R4 (Improve)", prompt);
        Assert.Contains("at most one Improve is allowed, positioned after the Review and before Merging", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_MultiRoundExampleDoesNotIncludeImprove()
    {
        var prompt = BuildPrompt();

        Assert.Contains("[\"coding\", \"testing\", \"coding\", \"testing\", \"review\", \"merging\"]", prompt);
        Assert.DoesNotContain("[\"coding\", \"testing\", \"coding\", \"testing\", \"review\", \"improve\", \"merging\"]", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ImproveRemainsInAvailablePhases()
    {
        var prompt = BuildPrompt();
        Assert.Contains("Available phases: coding, testing, docwriting, review, improve, merging", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsR5MergingRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R5 (Merging)", prompt);
        Assert.Contains("exactly one Merging", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsR6AllowedPhasesRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R6 (Allowed phases only)", prompt);
        Assert.Contains("only the six phase values", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsR7OrderingDependencyRule()
    {
        var prompt = BuildPrompt();
        Assert.Contains("R7 (Ordering-dependency)", prompt);
        Assert.Contains("Ordering-dependency", prompt);
        Assert.Contains("first Testing", prompt);
        Assert.Contains("first occurrences", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_R7DescribesSubmittedSequenceNotGoalNeeds()
    {
        var prompt = BuildPrompt();

        // R7 must describe the submitted plan's phase sequence, not the goal's requirements.
        Assert.Contains("when the submitted plan contains both Coding and DocWriting", prompt);
        Assert.Contains("exactly one of their first occurrences comes after the first Testing", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_ContainsPhaseNameRejectionRules()
    {
        var prompt = BuildPrompt();
        Assert.Contains("Phase-NAME rules", prompt);
        Assert.Contains("Unrecognized phase names:", prompt);
        Assert.Contains("Valid phases: coding, testing, docwriting, review, improve, merging.", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_PhaseNameRulesAreSeparateFromR6()
    {
        var prompt = BuildPrompt();

        var r6Index = prompt.IndexOf("R6 (Allowed phases only)");
        var phaseNameRulesIndex = prompt.IndexOf("Phase-NAME rules");

        Assert.NotEqual(-1, r6Index);
        Assert.NotEqual(-1, phaseNameRulesIndex);
        Assert.True(phaseNameRulesIndex > r6Index, "Phase-NAME rules should appear after R6, not be conflated with it.");

        // The unrecognized-name guidance paragraph itself must not contain the R6 label.
        var paragraphEnd = prompt.IndexOf("\n\n", phaseNameRulesIndex);
        var phaseNameParagraph = prompt[phaseNameRulesIndex..paragraphEnd];
        Assert.DoesNotContain("R6", phaseNameParagraph);
    }

    [Fact]
    public void BuildPlanningPrompt_StatesNoAutoFixBehavior()
    {
        var prompt = BuildPrompt();
        Assert.Contains("does NOT auto-fix", prompt);
        Assert.Contains("bounded attempts", prompt);
        Assert.Contains("no default-plan fallback", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_DoesNotContainStaleWording()
    {
        var prompt = BuildPrompt();
        Assert.DoesNotContain("may skip testing", prompt);
        Assert.DoesNotContain("auto-insert", prompt);
        Assert.DoesNotContain("auto-adjust", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_DocsOnlyChangeClarifiesTestingIsRequired()
    {
        var prompt = BuildPrompt();
        Assert.DoesNotContain("may skip testing", prompt);
        Assert.Contains("DocWriting → Testing → Review → Merging", prompt);
        Assert.Contains("Testing is always required after each content block per R2", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_DocsOnlyLineRemovesOldCoderThenDocwriterPhrasing()
    {
        var prompt = BuildPrompt();

        // The iteration-2 fix replaced "coder edits, then docwriter — may skip testing"
        // with "docwriter edits — a docs-only plan is DocWriting → Testing → Review → Merging".
        // Assert the old stale phrasing is gone and the new one is present.
        Assert.DoesNotContain("coder edits, then docwriter", prompt);
        Assert.Contains("docwriter edits", prompt);
        Assert.Contains("docs-only plan is DocWriting → Testing → Review → Merging", prompt);
    }

    // ── Additional gap-coverage tests ─────────────────────────────────────

    [Fact]
    public void BuildPlanningPrompt_ContainsExactRejectionMessageFormat()
    {
        var prompt = BuildPrompt();

        // The full rejection message template must appear as a single contiguous string
        // so the Brain can quote it verbatim when an unrecognized name is rejected.
        Assert.Contains(
            "Unrecognized phase names: <names>. Valid phases: coding, testing, docwriting, review, improve, merging.",
            prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_RejectsLifecycleAndNumericNames()
    {
        var prompt = BuildPrompt();

        // The prompt must name the lifecycle states and bare numeric tokens that are
        // rejected as unrecognized, so the Brain does not confuse them with valid phases.
        Assert.Contains("Planning", prompt);
        Assert.Contains("Done", prompt);
        Assert.Contains("Failed", prompt);
        Assert.Contains("numeric token", prompt);
        Assert.Contains("\"1\"", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_StatesNoReorderInsertOrFill()
    {
        var prompt = BuildPrompt();

        // The goal requires the precise "no-auto-fix/no-reorder/no-insert/no-fill"
        // wording, not a vague "does not alter".
        Assert.Contains("reorder", prompt);
        Assert.Contains("insert", prompt);
        Assert.Contains("fill structural phases", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_StatesResubmitViaReportIterationPlan()
    {
        var prompt = BuildPrompt();

        Assert.Contains("report_iteration_plan", prompt);
        Assert.Contains("resubmit", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_R7DoesNotOverstateAsNeverInterleave()
    {
        var prompt = BuildPrompt();

        // R7 must NOT be stated as a blanket "never interleave" rule. The prompt
        // must explicitly negate this overstatement.
        Assert.Contains("NOT a blanket", prompt);
        Assert.Contains("\"never interleave\"", prompt);
        Assert.Contains("prohibition", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_StaleAutoFixPhrasingsAbsent()
    {
        var prompt = BuildPrompt();

        // Goal requires "no-auto-fix/no-reorder/no-insert" — the vague "does not alter"
        // phrasing must not be used as a stand-in.
        Assert.DoesNotContain("does not alter", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auto-correct", prompt, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auto-reorder", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildPlanningPrompt_R7PrefersSingleBlockBeforeTesting()
    {
        var prompt = BuildPrompt();

        // The preferred fix must describe consolidating both content phases into a
        // single block before the Testing.
        Assert.Contains("single block", prompt);
        Assert.Contains("before that Testing", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_AcceptedPlanRunsInSubmittedOrder()
    {
        var prompt = BuildPrompt();

        // The prompt must state that an accepted (recognized) phase sequence runs in
        // the exact order submitted.
        Assert.Contains("runs in the exact order you submit it", prompt);
    }

    [Fact]
    public void BuildPlanningPrompt_OccurrenceSuffixesNormalizedNotFixing()
    {
        var prompt = BuildPrompt();

        // Suffix normalization and name-mapping are input parsing, not plan fixing.
        Assert.Contains("coding-2", prompt);
        Assert.Contains("normalized to the base name", prompt);
        Assert.Contains("input parsing", prompt);
    }

    // ── model_tiers guidance: Merging is a plan phase but NOT a tier key ─────

    /// <summary>
    /// Regression guard for the model_tiers guidance. This is the ONLY test that fails if the
    /// "Merging is a plan phase but NOT a tier key" sentence is deleted from the prompt, so
    /// reverting that guidance can no longer leave the suite green.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_ModelTiersSectionStatesMergingIsNotTierable()
    {
        var prompt = BuildPromptCollapsed();

        // The exact guidance sentence, matched whole so deleting any part of it fails the test.
        Assert.Contains(
            "Merging is a plan phase but NOT a tier key: `merging` must NEVER appear in model_tiers.",
            prompt);

        // The tierable key list is stated as the ONLY keys model_tiers accepts.
        Assert.Contains(
            "Tierable keys — the ONLY keys allowed here — are: coding, testing, docwriting, review, improve.",
            prompt);
        Assert.Contains(
            "only coding/testing/docwriting/review/improve may appear in model_tiers.",
            prompt);
    }

    /// <summary>
    /// The model_tiers guidance and R5 must COEXIST: excluding Merging from model_tiers must not
    /// be read as demoting Merging from its required final-plan-phase status.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_MergingExcludedFromTiersButStillRequiredFinalPhase()
    {
        var prompt = BuildPromptCollapsed();

        // R5 still declares Merging a required, final plan phase.
        Assert.Contains(
            "R5 (Merging): exactly one Merging is required, and it must be the final phase of the plan.",
            prompt);

        // Merging is still an available plan phase name.
        Assert.Contains("Available phases: coding, testing, docwriting, review, improve, merging", prompt);

        // …and the model_tiers bullet itself tells the Brain to KEEP Merging in `phases`
        // while removing it from model_tiers — proving the two statements coexist.
        Assert.Contains(
            "Keep Merging in `phases` (R5 still requires it as the final phase);",
            prompt);

        // The tierable-key list must never include merging.
        Assert.DoesNotContain(
            "Tierable keys — the ONLY keys allowed here — are: coding, testing, docwriting, review, improve, merging",
            prompt);
    }

    /// <summary>
    /// The Merging-is-not-a-tier-key guidance must live in the model_tiers bullet (not somewhere
    /// unrelated), and must come after the phase-name rules that govern the `phases` array.
    /// </summary>
    [Fact]
    public void BuildPlanningPrompt_MergingTierGuidanceLivesInModelTiersBullet()
    {
        var prompt = BuildPromptCollapsed();

        var modelTiersIndex = prompt.IndexOf("- model_tiers: (optional)", StringComparison.Ordinal);
        var guidanceIndex = prompt.IndexOf(
            "Merging is a plan phase but NOT a tier key", StringComparison.Ordinal);
        var premiumIndex = prompt.IndexOf(
            "Only use premium when previous iterations failed", StringComparison.Ordinal);

        Assert.NotEqual(-1, modelTiersIndex);
        Assert.NotEqual(-1, guidanceIndex);
        Assert.NotEqual(-1, premiumIndex);

        // The guidance sits inside the model_tiers bullet body.
        Assert.True(
            guidanceIndex > modelTiersIndex && guidanceIndex < premiumIndex,
            "The Merging-is-not-a-tier-key guidance must appear inside the model_tiers bullet.");

        // Phase-NAME rules (which govern `phases`) explicitly distinguish themselves from
        // the model_tiers KEY rules.
        Assert.Contains("Phase-NAME rules (these govern the `phases` array", prompt);
        Assert.Contains("`model_tiers` KEY rules", prompt);
    }
}
